using System;
using System.Windows;
using boston_timing_system.Services;

namespace boston_timing_system.Views
{
    public partial class QrCodeConnectionWindow : Window
    {
        private readonly string _ipAddress;
        private readonly int _port;
        private readonly string _accessCode;
        private readonly int _webPort;
        private readonly QrCodeService _qrService = new();
        private string _currentPayload = string.Empty;

        public QrCodeConnectionWindow(string ipAddress, int port, string accessCode, int webPort = 3000)
        {
            InitializeComponent();

            _ipAddress = string.IsNullOrWhiteSpace(ipAddress) ? "127.0.0.1" : ipAddress;
            _port = port > 0 ? port : 8181;
            _accessCode = string.IsNullOrWhiteSpace(accessCode) ? "1000" : accessCode;
            _webPort = webPort;

            txtInfoIp.Text = $"{_ipAddress}:{_port}";
            txtInfoCode.Text = _accessCode;

            Loaded += (s, e) => UpdateQrCode();
        }

        private void UpdateQrCode()
        {
            if (imgQrCode == null) return;

            _currentPayload = _qrService.BuildJsonPayload(_ipAddress, _port, _accessCode);

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
