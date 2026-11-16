using Microsoft.Win32;
using System.IO;
using System;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.Diagnostics;
using System.Security.Principal;

namespace TFTP_KYRSOVAJA;

public partial class MainWindow : Window
{
    private TftpServer _tftpServer;
    private readonly TftpClient _tftpClient;

    public MainWindow()
    {
        InitializeComponent();
        _tftpClient = new TftpClient();
        _tftpClient.LogMessage += message => Dispatcher.Invoke(() => ClientLogTextBox.AppendText(message + Environment.NewLine));
    }

    private void StartServerButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var rootDirectory = ServerRootDirectoryTextBox.Text;
            if (!Directory.Exists(rootDirectory))
            {
                MessageBox.Show("The specified root directory does not exist.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            _tftpServer = new TftpServer(rootDirectory);
            _tftpServer.LogMessage += message => Dispatcher.Invoke(() => ServerLogTextBox.AppendText(message + Environment.NewLine));
            _tftpServer.Start();

            StartServerButton.IsEnabled = false;
            StopServerButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error starting server: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void StopServerButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _tftpServer?.Stop();
            StartServerButton.IsEnabled = true;
            StopServerButton.IsEnabled = false;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error stopping server: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void DownloadButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var serverIp = ServerIpTextBox.Text;
            var remoteFile = RemoteFileTextBox.Text;
            var localFile = LocalFileTextBox.Text;

            if (string.IsNullOrWhiteSpace(serverIp)) throw new ArgumentException("Server IP is required");
            if (string.IsNullOrWhiteSpace(remoteFile)) throw new ArgumentException("Remote file is required");
            if (string.IsNullOrWhiteSpace(localFile)) throw new ArgumentException("Local file is required");

            await _tftpClient.DownloadFileAsync(serverIp, remoteFile, localFile);
            MessageBox.Show("Download completed successfully", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Download error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void UploadButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var serverIp = ServerIpTextBox.Text;
            var localFile = LocalFileTextBox.Text;
            var remoteFile = RemoteFileTextBox.Text;

            if (string.IsNullOrWhiteSpace(serverIp)) throw new ArgumentException("Server IP is required");
            if (string.IsNullOrWhiteSpace(localFile)) throw new ArgumentException("Local file is required");
            if (string.IsNullOrWhiteSpace(remoteFile)) throw new ArgumentException("Remote file is required");
            if (!File.Exists(localFile)) throw new FileNotFoundException("Local file not found", localFile);

            await _tftpClient.UploadFileAsync(serverIp, localFile, remoteFile);
            MessageBox.Show("Upload completed successfully", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Upload error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BrowseServerDirectoryButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select TFTP root directory",
            Multiselect = false
        };

        if (dialog.ShowDialog() == true)
        {
            ServerRootDirectoryTextBox.Text = dialog.FolderName;
        }
    }

    private void BrowseLocalFileButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog();
        if (dialog.ShowDialog() == true)
        {
            LocalFileTextBox.Text = dialog.FileName;
        }
    }

    private void ClearServerLogButton_Click(object sender, RoutedEventArgs e)
    {
        ServerLogTextBox.Clear();
    }

    private void ClearClientLogButton_Click(object sender, RoutedEventArgs e)
    {
        ClientLogTextBox.Clear();
    }

    protected override void OnClosed(EventArgs e)
    {
        _tftpServer?.Stop();
        base.OnClosed(e);
    }
}