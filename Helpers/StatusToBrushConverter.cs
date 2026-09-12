using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using boston_timing_system.Models;

namespace boston_timing_system.Helpers
{
    public class StatusToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is LaneStatus status)
            {
                return status switch
                {
                    LaneStatus.Ready => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3B82F6")),     // Blue
                    LaneStatus.Running => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#10B981")),   // Green
                    LaneStatus.Finished => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F59E0B")),  // Amber / Gold
                    LaneStatus.DNS => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#6B7280")),       // Gray
                    LaneStatus.DNF => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#EF4444")),       // Red
                    LaneStatus.DQ => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#DC2626")),        // Dark Red
                    LaneStatus.OFF => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#374151")),       // Muted Dark Gray
                    _ => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4B5563"))
                };
            }

            if (value is RaceStatus raceStatus)
            {
                return raceStatus switch
                {
                    RaceStatus.Ready => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3B82F6")),
                    RaceStatus.Running => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#10B981")),
                    RaceStatus.Finished => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F59E0B")),
                    _ => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#6B7280"))
                };
            }

            if (value is string statusStr)
            {
                return statusStr switch
                {
                    "DQ" => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#DC2626")),
                    "DNS" => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#D97706")),
                    "DNF" => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#EF4444")),
                    "OFF" => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#6B7280")),
                    _ => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#94A3B8"))
                };
            }

            return new SolidColorBrush(Colors.Transparent);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
