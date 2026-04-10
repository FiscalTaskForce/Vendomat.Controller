using Microsoft.Maui.ApplicationModel;
using System.ComponentModel;
using Vendomat.Controller.Mobile.Models;
using Vendomat.Controller.Mobile.ViewModels;
using ZXing.Net.Maui;
using ZXing.Net.Maui.Controls;

namespace Vendomat.Controller.Mobile.Pages;

public partial class QrScannerPage : ContentPage, IQueryAttributable
{
    private ManualPairingLaunchRequest? _pendingManualRequest;
    private CameraBarcodeReaderView? _barcodeReader;

    private QrScannerViewModel ViewModel => (QrScannerViewModel)BindingContext;

    public QrScannerPage()
    {
        InitializeComponent();
        BindingContext = ServiceRegistry.GetRequiredService<QrScannerViewModel>();
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        var request = new ManualPairingLaunchRequest
        {
            RawPayload = ReadQueryValue(query, "rawPayload"),
            MachineId = ReadQueryValue(query, "machineId"),
            PairingCode = ReadQueryValue(query, "pairingCode"),
            CloudApiBaseUrl = ReadQueryValue(query, "cloudApiBaseUrl"),
            PublicApiBaseUrl = ReadQueryValue(query, "publicApiBaseUrl"),
            LocalApiBaseUrl = ReadQueryValue(query, "localApiBaseUrl"),
            AutoSubmit = string.Equals(ReadQueryValue(query, "autoSubmit"), "1", StringComparison.Ordinal),
        };

        var manualMode = string.Equals(ReadQueryValue(query, "manualMode"), "1", StringComparison.Ordinal);
        _pendingManualRequest = manualMode || request.HasAnyValue ? request : null;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        ViewModel.Reset();

        if (_pendingManualRequest is not null)
        {
            var paired = await ViewModel.ApplyManualLaunchRequestAsync(_pendingManualRequest);
            _pendingManualRequest = null;
            if (paired)
            {
                await Shell.Current.GoToAsync("//devices");
                return;
            }
        }

        if (DeviceInfo.DeviceType == DeviceType.Virtual)
        {
            ScannerUnavailableLabel.IsVisible = true;
            return;
        }

        await StartScannerAsync(requestPermission: true);
    }

    protected override void OnDisappearing()
    {
        StopScanner();
        base.OnDisappearing();
    }

    private async void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.Equals(e.PropertyName, nameof(QrScannerViewModel.IsManualPairingVisible), StringComparison.Ordinal))
        {
            return;
        }

        await StartScannerAsync(requestPermission: true);
    }

    private async void OnBarcodesDetected(object? sender, BarcodeDetectionEventArgs e)
    {
        var value = e.Results.FirstOrDefault()?.Value;
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        StopScanner();
        var paired = await MainThread.InvokeOnMainThreadAsync(() => ViewModel.HandleScanAsync(value));
        if (paired)
        {
            await Shell.Current.GoToAsync("//devices");
            return;
        }

        await StartScannerAsync(requestPermission: false);
    }

    private async void OnBackClicked(object? sender, EventArgs e)
    {
        StopScanner();
        await Shell.Current.GoToAsync("//devices");
    }

    private static string ReadQueryValue(IDictionary<string, object> query, string key)
    {
        if (!query.TryGetValue(key, out var rawValue))
        {
            return string.Empty;
        }

        return rawValue?.ToString()?.Trim() ?? string.Empty;
    }

    private bool EnsureScannerCreated()
    {
        if (_barcodeReader is not null)
        {
            return true;
        }

        _barcodeReader = new CameraBarcodeReaderView
        {
            Margin = new Thickness(10),
            HeightRequest = 360,
            HorizontalOptions = LayoutOptions.Fill,
            VerticalOptions = LayoutOptions.Fill,
            IsDetecting = false,
            Options = new BarcodeReaderOptions
            {
                Formats = BarcodeFormats.TwoDimensional,
                AutoRotate = true,
                Multiple = false,
            },
        };

        _barcodeReader.BarcodesDetected += OnBarcodesDetected;
        ScannerHost.Children.Clear();
        ScannerHost.Children.Add(_barcodeReader);
        ScannerUnavailableLabel.IsVisible = false;
        return true;
    }

    private void StopScanner()
    {
        if (_barcodeReader is not null)
        {
            _barcodeReader.IsDetecting = false;
        }
    }

    private async Task StartScannerAsync(bool requestPermission)
    {
        if (!EnsureScannerCreated())
        {
            return;
        }

        var permission = requestPermission
            ? await Permissions.RequestAsync<Permissions.Camera>()
            : await Permissions.CheckStatusAsync<Permissions.Camera>();

        if (permission == PermissionStatus.Granted)
        {
            ScannerUnavailableLabel.IsVisible = false;
            _barcodeReader!.IsDetecting = true;
            return;
        }

        StopScanner();
        ScannerUnavailableLabel.IsVisible = true;
        ViewModel.StatusMessage = ViewModel.GetPermissionHintMessage();
    }
}
