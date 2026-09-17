using System;
using System.Windows;
using boston_timing_system.Models;
using boston_timing_system.Services;

namespace boston_timing_system.Views
{
    public partial class QrCodeConnectionWindow : Window
    {
        private readonly string _ipAddress;
        private readonly int _port;
        private readonly string _accessCode;
        private readonly int _webPort;
        private readonly TimingMode _timingMode;
        private readonly QrCodeService _qrService = new();
        private string _currentPayload = string.Empty;

        public QrCodeConnectionWindow(string ipAddress, int port, string accessCode, int webPort = 3000, TimingMode timingMode = TimingMode.Pool)
        {
            InitializeComponent();

            _ipAddress = string.IsNullOrWhiteSpace(ipAddress) ? "127.0.0.1" : ipAddress;
            _port = port > 0 ? port : 8181;
            _accessCode = string.IsNullOrWhiteSpace(accessCode) ? "1000" : accessCode;
            _webPort = webPort;
            _timingMode = timingMode;

            txtInfoIp.Text = $"{_ipAddress}:{_port}";
            txtInfoCode.Text = _accessCode;

            if (_timingMode == TimingMode.OpenWater)
            {
                txtQrSubtitle.Text = "Scan dengan HP untuk menghubungkan Wasit Finis (OWS) atau Starter (Chief tidak tersedia di OWS)";
            }
            else
            {
                txtQrSubtitle.Text = "Scan dengan HP untuk menghubungkan Wasit, Starter, atau Chief";
            }

            Loaded += (s, e) => UpdateQrCode();
        }

        private void UpdateQrCode()
        {
            if (imgQrCode == null) return;

            string modeStr = _timingMode == TimingMode.OpenWater ? "OPEN_WATER" : "POOL";
            _currentPayload = _qrService.BuildJsonPayload(_ipAddress, _port, _accessCode, modeStr);

            try
            {
                imgQrCode.Source = _qrService.GenerateQrCodeImage(_currentPayload, 12);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Gagal membuat QR Code: {ex.Message}", "QR Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
