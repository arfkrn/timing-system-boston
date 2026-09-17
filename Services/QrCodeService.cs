using System;
using System.IO;
using System.Windows.Media.Imaging;
using QRCoder;

namespace boston_timing_system.Services
{
    public class QrCodeService
    {
        /// <summary>
        /// Generates a WPF BitmapImage containing the QR Code for the specified payload.
        /// </summary>
        public BitmapImage GenerateQrCodeImage(string payload, int pixelsPerModule = 10)
        {
            if (string.IsNullOrWhiteSpace(payload))
            {
                payload = "{}";
            }

            using var qrGenerator = new QRCodeGenerator();
            using var qrCodeData = qrGenerator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.M);
            var qrCode = new PngByteQRCode(qrCodeData);
            byte[] qrBytes = qrCode.GetGraphic(pixelsPerModule);

            using var ms = new MemoryStream(qrBytes);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = ms;
            bitmap.EndInit();
            bitmap.Freeze();

            return bitmap;
        }

        /// <summary>
        /// Builds standard JSON connection payload containing server IP, WebSocket port, access code, and timing mode.
        /// </summary>
        public string BuildJsonPayload(string ip, int port, string accessCode, string timingMode = "POOL")
        {
            return $"{{\"ip\":\"{ip}\",\"port\":{port},\"accessCode\":\"{accessCode}\",\"wsUri\":\"ws://{ip}:{port}\",\"timingMode\":\"{timingMode}\"}}";
        }

        /// <summary>
        /// Builds mobile web URL for direct browser access.
        /// </summary>
        public string BuildWebUrlPayload(string ip, int webPort, string accessCode)
        {
            return $"http://{ip}:{webPort}/mobile.html?code={accessCode}&ip={ip}";
        }
    }
}
