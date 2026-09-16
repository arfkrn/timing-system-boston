using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using boston_timing_system.Models;

namespace boston_timing_system.Services
{
    /// <summary>
    /// DTO wrapper for saving the complete workspace state (session auto-save & crash recovery).
    /// </summary>
    public class MeetSessionDto
    {
        public int SchemaVersion { get; set; } = 1;
        public DateTime SavedAt { get; set; } = DateTime.Now;
        public TimingMode ActiveTimingMode { get; set; } = TimingMode.Pool;
        public CompetitionMeetModel PoolMeet { get; set; } = new();
        public CompetitionMeetModel OwsMeet { get; set; } = new();
        public List<OwsRecordModel> ActiveOwsRecords { get; set; } = new();
    }

    /// <summary>
    /// DTO wrapper for saving a single active meet project file (.bts / .json).
    /// </summary>
    public class MeetProjectDto
    {
        public int SchemaVersion { get; set; } = 1;
        public DateTime ExportedAt { get; set; } = DateTime.Now;
        public TimingMode TimingMode { get; set; } = TimingMode.Pool;
        public CompetitionMeetModel Meet { get; set; } = new();
    }

    /// <summary>
    /// Service responsible for persistent storage of competition meets and active timing sessions.
    /// Implements safe atomic writes, file rotation backups (.bak), and non-blocking I/O.
    /// </summary>
    public class MeetPersistenceService
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };

        private readonly string _dataDirectory;
        private readonly string _autoSaveFilePath;
        private readonly string _autoSaveBackupPath;

        private readonly SemaphoreSlim _ioLock = new(1, 1);
        private CancellationTokenSource? _debounceCts;
        private readonly object _debounceLock = new();

        public string DataDirectory => _dataDirectory;
        public string AutoSaveFilePath => _autoSaveFilePath;

        public event Action<string>? AutoSaveStatusChanged;

        public MeetPersistenceService(string? customDataDirectory = null)
        {
            _dataDirectory = !string.IsNullOrWhiteSpace(customDataDirectory)
                ? customDataDirectory
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "BostonTimingSystem");

            _autoSaveFilePath = Path.Combine(_dataDirectory, "autosave_session.json");
            _autoSaveBackupPath = Path.Combine(_dataDirectory, "autosave_session.json.bak");

            try
            {
                if (!Directory.Exists(_dataDirectory))
                {
                    Directory.CreateDirectory(_dataDirectory);
                }
            }
            catch
            {
                // Silently ignore directory creation errors in constructor; will retry during write
            }
        }

        #region Session Auto-Save & Recovery

        /// <summary>
        /// Checks if an auto-save session file exists and is readable.
        /// </summary>
        public bool HasAutoSaveSession()
        {
            try
            {
                return File.Exists(_autoSaveFilePath) && new FileInfo(_autoSaveFilePath).Length > 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Gets the timestamp of the existing auto-save session, or null if none exists.
        /// </summary>
        public DateTime? GetAutoSaveTimestamp()
        {
            try
            {
                if (File.Exists(_autoSaveFilePath))
                {
                    return File.GetLastWriteTime(_autoSaveFilePath);
                }
            }
            catch
            {
                // ignore
            }
            return null;
        }

        /// <summary>
        /// Requests a debounced background auto-save.
        /// Subsequent calls within delayMs are coalesced into a single disk write.
        /// </summary>
        public void RequestDebouncedAutoSave(MeetSessionDto session, int delayMs = 600)
        {
            lock (_debounceLock)
            {
                _debounceCts?.Cancel();
                _debounceCts?.Dispose();
                _debounceCts = new CancellationTokenSource();
                var token = _debounceCts.Token;

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(delayMs, token);
                        if (!token.IsCancellationRequested)
                        {
                            await SaveSessionAsync(session);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // Expected when superseded by a newer save request
                    }
                    catch (Exception ex)
                    {
                        AutoSaveStatusChanged?.Invoke($"Auto-save error: {ex.Message}");
                    }
                }, token);
            }
        }

        /// <summary>
        /// Immediately saves the current session to the autosave file using atomic write.
        /// </summary>
        public async Task SaveSessionAsync(MeetSessionDto session)
        {
            session.SavedAt = DateTime.Now;
            string json = JsonSerializer.Serialize(session, JsonOptions);

            await _ioLock.WaitAsync();
            try
            {
                await AtomicWriteAsync(_autoSaveFilePath, json);
                AutoSaveStatusChanged?.Invoke($"Saved at {session.SavedAt:HH:mm:ss}");
            }
            finally
            {
                _ioLock.Release();
            }
        }

        /// <summary>
        /// Synchronous immediate save used during window closing to guarantee data is saved before process termination.
        /// </summary>
        public void SaveSessionImmediate(MeetSessionDto session)
        {
            try
            {
                lock (_debounceLock)
                {
                    _debounceCts?.Cancel();
                }

                session.SavedAt = DateTime.Now;
                string json = JsonSerializer.Serialize(session, JsonOptions);
                AtomicWriteSync(_autoSaveFilePath, json);
            }
            catch
            {
                // best-effort save on shutdown
            }
        }

        /// <summary>
        /// Loads the auto-saved session. Falls back to the backup (.bak) file if the main file is corrupt.
        /// </summary>
        public async Task<MeetSessionDto?> LoadAutoSaveSessionAsync()
        {
            await _ioLock.WaitAsync();
            try
            {
                if (File.Exists(_autoSaveFilePath))
                {
                    try
                    {
                        string json = await File.ReadAllTextAsync(_autoSaveFilePath, Encoding.UTF8);
                        var session = JsonSerializer.Deserialize<MeetSessionDto>(json, JsonOptions);
                        if (session != null) return session;
                    }
                    catch
                    {
                        // Main file may be corrupt; attempt fallback to .bak
                    }
                }

                if (File.Exists(_autoSaveBackupPath))
                {
                    try
                    {
                        string backupJson = await File.ReadAllTextAsync(_autoSaveBackupPath, Encoding.UTF8);
                        return JsonSerializer.Deserialize<MeetSessionDto>(backupJson, JsonOptions);
                    }
                    catch
                    {
                        // Fallback also failed
                    }
                }

                return null;
            }
            finally
            {
                _ioLock.Release();
            }
        }

        /// <summary>
        /// Deletes auto-save files when the user explicitly chooses to discard or start a clean meet.
        /// </summary>
        public void ClearAutoSaveSession()
        {
            try
            {
                if (File.Exists(_autoSaveFilePath))
                {
                    File.Delete(_autoSaveFilePath);
                }
                if (File.Exists(_autoSaveBackupPath))
                {
                    File.Delete(_autoSaveBackupPath);
                }
            }
            catch
            {
                // ignore
            }
        }

        #endregion

        #region Manual Meet Project File (Save / Open .bts or .json)

        /// <summary>
        /// Saves a single active competition meet to a user-specified file (.bts or .json).
        /// </summary>
        public async Task SaveMeetToFileAsync(CompetitionMeetModel meet, TimingMode mode, string filePath)
        {
            var project = new MeetProjectDto
            {
                ExportedAt = DateTime.Now,
                TimingMode = mode,
                Meet = meet
            };

            string json = JsonSerializer.Serialize(project, JsonOptions);

            await _ioLock.WaitAsync();
            try
            {
                await AtomicWriteAsync(filePath, json);
            }
            finally
            {
                _ioLock.Release();
            }
        }

        /// <summary>
        /// Loads a meet project from a user-specified file (.bts or .json).
        /// Supports both MeetProjectDto wrapped format and direct CompetitionMeetModel format.
        /// </summary>
        public async Task<MeetProjectDto?> LoadMeetFromFileAsync(string filePath)
        {
            if (!File.Exists(filePath)) return null;

            await _ioLock.WaitAsync();
            try
            {
                string json = await File.ReadAllTextAsync(filePath, Encoding.UTF8);

                // 1. Try deserializing as MeetProjectDto
                try
                {
                    var project = JsonSerializer.Deserialize<MeetProjectDto>(json, JsonOptions);
                    if (project?.Meet != null && (project.Meet.Events.Count > 0 || !string.IsNullOrWhiteSpace(project.Meet.MeetName)))
                    {
                        return project;
                    }
                }
                catch
                {
                    // Fall back to direct CompetitionMeetModel
                }

                // 2. Try deserializing directly as CompetitionMeetModel
                var directMeet = JsonSerializer.Deserialize<CompetitionMeetModel>(json, JsonOptions);
                if (directMeet != null)
                {
                    return new MeetProjectDto
                    {
                        TimingMode = TimingMode.Pool,
                        Meet = directMeet
                    };
                }

                return null;
            }
            finally
            {
                _ioLock.Release();
            }
        }

        #endregion

        #region Safe Atomic Write Helpers

        private static async Task AtomicWriteAsync(string targetFilePath, string jsonContent)
        {
            string? dir = Path.GetDirectoryName(targetFilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string tempPath = targetFilePath + $".tmp_{Guid.NewGuid():N}";
            string backupPath = targetFilePath + ".bak";

            await File.WriteAllTextAsync(tempPath, jsonContent, Encoding.UTF8);

            if (File.Exists(targetFilePath))
            {
                try
                {
                    File.Copy(targetFilePath, backupPath, overwrite: true);
                }
                catch
                {
                    // Non-critical: backup creation failed
                }
            }

            File.Move(tempPath, targetFilePath, overwrite: true);
        }

        private static void AtomicWriteSync(string targetFilePath, string jsonContent)
        {
            string? dir = Path.GetDirectoryName(targetFilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string tempPath = targetFilePath + $".tmp_{Guid.NewGuid():N}";
            string backupPath = targetFilePath + ".bak";

            File.WriteAllText(tempPath, jsonContent, Encoding.UTF8);

            if (File.Exists(targetFilePath))
            {
                try
                {
                    File.Copy(targetFilePath, backupPath, overwrite: true);
                }
                catch
                {
                    // ignore
                }
            }

            File.Move(tempPath, targetFilePath, overwrite: true);
        }

        #endregion
    }
}
