using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using boston_timing_system.Models;
using boston_timing_system.Services;

namespace boston_timing_system.Views
{
    public partial class ThermalPrintWindow : Window
    {
        private readonly CompetitionMeetModel _meet;
        private readonly HeatModel _heat;
        private readonly ThermalPrintService _printService;
        private readonly TimingMode _timingMode;
        private FrameworkElement? _receiptVisual;
        private string? _receiptText;

        public bool PrintSuccessful { get; private set; }

        public ThermalPrintWindow(CompetitionMeetModel meet, HeatModel heat, ThermalPrintService printService, TimingMode timingMode = TimingMode.Pool)
        {
            InitializeComponent();

            _meet = meet ?? throw new ArgumentNullException(nameof(meet));
            _heat = heat ?? throw new ArgumentNullException(nameof(heat));
            _printService = printService ?? throw new ArgumentNullException(nameof(printService));
            _timingMode = timingMode;

            Loaded += ThermalPrintWindow_Loaded;
            Closed += (s, e) =>
            {
                bdReceiptContainer.Child = null;
                _receiptVisual = null;
            };
        }

        private void ThermalPrintWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // Populate Meet and Event/Heat Information
            txtInfoMeet.Text = _meet.MeetName;
            txtInfoEventHeat.Text = $"Event #{_heat.EventNumber} ({_heat.EventName}) - Heat #{_heat.HeatNumber}";

            // Update badge text if OWS
            if (_timingMode == TimingMode.OpenWater && txtFormatBadge != null)
            {
                txtFormatBadge.Text = "FORMAT: 58MM OWS ROLL";
            }

            // Generate receipt preview visual
            _receiptVisual = _printService.CreateReceiptVisual(_meet, _heat, _timingMode);
            bdReceiptContainer.Child = _receiptVisual;

            // Generate monospace text version
            _receiptText = _printService.GenerateReceiptText(_meet, _heat, _timingMode);

            // Populate installed printers
            LoadPrinters();
        }

        private void LoadPrinters()
        {
            var printers = _printService.GetInstalledPrinters();
            cmbPrinters.Items.Clear();

            if (printers.Count == 0)
            {
                cmbPrinters.Items.Add("Default Printer");
                cmbPrinters.SelectedIndex = 0;
                txtPrinterStatus.Text = "Using Windows system default printer.";
                txtPrinterStatus.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#475569"));
                return;
            }

            foreach (var printer in printers)
            {
                cmbPrinters.Items.Add(printer);
            }

            // Find recommended 58mm printer
            string? recommended = _printService.FindRecommendedThermalPrinter(printers);
            if (!string.IsNullOrWhiteSpace(recommended) && cmbPrinters.Items.Contains(recommended))
            {
                cmbPrinters.SelectedItem = recommended;
                txtPrinterStatus.Text = $"Thermal printer detected: {recommended}";
                txtPrinterStatus.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#16A34A"));
            }
            else
            {
                cmbPrinters.SelectedIndex = 0;
                txtPrinterStatus.Text = $"Ready to print to: {cmbPrinters.SelectedItem}";
                txtPrinterStatus.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#475569"));
            }
        }

        private void CmbPrinters_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (cmbPrinters.SelectedItem is string selectedPrinter)
            {
                string lower = selectedPrinter.ToLowerInvariant();
                if (lower.Contains("58") || lower.Contains("pos") || lower.Contains("thermal") || lower.Contains("receipt"))
                {
                    txtPrinterStatus.Text = $"✓ Selected thermal printer: {selectedPrinter}";
                    txtPrinterStatus.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#16A34A"));
                }
                else
                {
                    txtPrinterStatus.Text = $"Selected printer: {selectedPrinter}";
                    txtPrinterStatus.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#475569"));
                }
            }
        }

        private void BtnPrintNow_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string? selectedPrinter = cmbPrinters.SelectedItem as string;
                if (selectedPrinter == "Default Printer")
                {
                    selectedPrinter = null;
                }

                // Create fresh visual specifically for printing to avoid visual tree re-parenting issues
                var printVisual = _printService.CreateReceiptVisual(_meet, _heat, _timingMode);
                var result = _printService.PrintVisualToPrinter(printVisual, selectedPrinter);

                if (result.Success)
                {
                    PrintSuccessful = true;
                    bdFeedback.Visibility = Visibility.Visible;
                    bdFeedback.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#DCFCE7"));
                    bdFeedback.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#86EFAC"));
                    txtFeedback.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#166534"));
                    txtFeedback.Text = $"SUCCESS: Race result slip sent to printer [{selectedPrinter ?? "Default"}]!";
                }
                else
                {
                    bdFeedback.Visibility = Visibility.Visible;
                    bdFeedback.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FEE2E2"));
                    bdFeedback.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FCA5A5"));
                    txtFeedback.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#991B1B"));
                    txtFeedback.Text = $"FAILED TO PRINT: {result.Message}";
                }
            }
            catch (Exception ex)
            {
                bdFeedback.Visibility = Visibility.Visible;
                bdFeedback.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FEE2E2"));
                bdFeedback.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FCA5A5"));
                txtFeedback.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#991B1B"));
                txtFeedback.Text = $"An error occurred: {ex.Message}";
            }
        }

        private void BtnCopyText_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(_receiptText))
            {
                Clipboard.SetText(_receiptText);
                bdFeedback.Visibility = Visibility.Visible;
                bdFeedback.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#EFF6FF"));
                bdFeedback.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#BFDBFE"));
                txtFeedback.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1E40AF"));
                txtFeedback.Text = "58mm receipt text (32 columns) copied to clipboard!";
            }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
