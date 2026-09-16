using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using boston_timing_system.Models;

namespace boston_timing_system.Helpers
{
    public class StatusToBrushConverter : IValueConverter
    {
        private static readonly SolidColorBrush BlueBrush = CreateFrozenBrush("#3B82F6");
        private static readonly SolidColorBrush GreenBrush = CreateFrozenBrush("#10B981");
        private static readonly SolidColorBrush AmberBrush = CreateFrozenBrush("#F59E0B");
        private static readonly SolidColorBrush DarkAmberBrush = CreateFrozenBrush("#D97706");
        private static readonly SolidColorBrush RedBrush = CreateFrozenBrush("#EF4444");
        private static readonly SolidColorBrush DarkRedBrush = CreateFrozenBrush("#DC2626");
        private static readonly SolidColorBrush GrayBrush = CreateFrozenBrush("#6B7280");
        private static readonly SolidColorBrush LightGrayBrush = CreateFrozenBrush("#94A3B8");
        private static readonly SolidColorBrush DarkGrayBrush = CreateFrozenBrush("#374151");
        private static readonly SolidColorBrush DefaultBrush = CreateFrozenBrush("#4B5563");
        private static readonly SolidColorBrush TransparentBrush = CreateFrozenBrush("#00000000");

        private static SolidColorBrush CreateFrozenBrush(string hex)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is LaneStatus status)
            {
                return status switch
                {
                    LaneStatus.Ready => BlueBrush,
                    LaneStatus.Running => GreenBrush,
                    LaneStatus.Finished => AmberBrush,
                    LaneStatus.DNS => GrayBrush,
                    LaneStatus.DNF => RedBrush,
                    LaneStatus.DQ => DarkRedBrush,
                    LaneStatus.OFF => DarkGrayBrush,
                    _ => DefaultBrush
                };
            }

            if (value is RaceStatus raceStatus)
            {
                return raceStatus switch
                {
                    RaceStatus.Ready => BlueBrush,
                    RaceStatus.Running => GreenBrush,
                    RaceStatus.Finished => AmberBrush,
                    _ => GrayBrush
                };
            }

            if (value is string statusStr)
            {
                return statusStr switch
                {
                    "Finished" => GreenBrush,
                    "DQ" => DarkRedBrush,
                    "DNS" => DarkAmberBrush,
                    "DNF" => RedBrush,
                    "OFF" => GrayBrush,
                    _ => LightGrayBrush
                };
            }

            return TransparentBrush;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
