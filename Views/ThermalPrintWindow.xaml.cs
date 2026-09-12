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
        private FrameworkElement? _receiptVisual;
        private string? _receiptText;

        public bool PrintSuccessful { get; private set; }

        public ThermalPrintWindow(CompetitionMeetModel meet, HeatModel heat, ThermalPrintService printService)
        {
            InitializeComponent();

            _meet = meet ?? throw new ArgumentNullException(nameof(meet));
            _heat = heat ?? throw new ArgumentNullException(nameof(heat));
            _printService = printService ?? throw new ArgumentNullException(nameof(printService));

            Loaded += ThermalPrintWindow_Loaded;
        }

        private void ThermalPrintWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // Populate Meet and Event/Heat Information
            txtInfoMeet.Text = _meet.MeetName;
            txtInfoEventHeat.Text = $"Event #{_heat.EventNumber} ({_heat.EventName}) - Heat #{_heat.HeatNumber}";

            // Generate receipt preview visual
            _receiptVisual = _printService.CreateReceiptVisual(_meet, _heat);
            bdReceiptContainer.Child = _receiptVisual;

            // Generate monospace text version
            _receiptText = _printService.GenerateReceiptText(_meet, _heat);

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
                txtPrinterStatus.Text = "Menggunakan default printer sistem Windows.";
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
                txtPrinterStatus.Text = $"Printer thermal terdeteksi: {recommended}";
                txtPrinterStatus.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#16A34A"));
            }
            else
            {
                cmbPrinters.SelectedIndex = 0;
                txtPrinterStatus.Text = $"Siap mencetak ke: {cmbPrinters.SelectedItem}";
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
                    txtPrinterStatus.Text = $"✓ Terpilih printer thermal: {selectedPrinter}";
                    txtPrinterStatus.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#16A34A"));
                }
                else
                {
                    txtPrinterStatus.Text = $"Pencetak terpilih: {selectedPrinter}";
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
                var printVisual = _printService.CreateReceiptVisual(_meet, _heat);
                var result = _printService.PrintVisualToPrinter(printVisual, selectedPrinter);

                if (result.Success)
                {
                    PrintSuccessful = true;
                    bdFeedback.Visibility = Visibility.Visible;
                    bdFeedback.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#DCFCE7"));
                    bdFeedback.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#86EFAC"));
                    txtFeedback.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#166534"));
                    txtFeedback.Text = $"BERHASIL: Struk hasil balapan berhasil dikirim ke printer [{selectedPrinter ?? "Default"}]!";
                }
                else
                {
                    bdFeedback.Visibility = Visibility.Visible;
                    bdFeedback.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FEE2E2"));
                    bdFeedback.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FCA5A5"));
                    txtFeedback.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#991B1B"));
                    txtFeedback.Text = $"GAGAL MENCETAK: {result.Message}";
                }
            }
            catch (Exception ex)
            {
                bdFeedback.Visibility = Visibility.Visible;
                bdFeedback.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FEE2E2"));
                bdFeedback.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FCA5A5"));
                txtFeedback.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#991B1B"));
                txtFeedback.Text = $"Terjadi kesalahan: {ex.Message}";
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
                txtFeedback.Text = "Teks struk 58mm (32 kolom) berhasil disalin ke clipboard!";
            }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
