using Vendomat.Controller.Tablet.Pages;
using Vendomat.Controller.Tablet.ViewModels;
using Vendomat.Controller.Client.Localization;
using System.ComponentModel;

namespace Vendomat.Controller.Tablet;

public partial class MainPage : ContentPage
{
    private const int AdminPasscodeMaxLength = 8;
    private static readonly TimeSpan AdminHotspotHoldDuration = TimeSpan.FromSeconds(5);
    private static readonly Color AdminHotspotActiveColor = Color.FromArgb("#88F59E0B");
    private static readonly Color AdminHotspotReadyColor = Color.FromArgb("#8835B36B");

    private CancellationTokenSource? _adminHotspotHoldCancellationTokenSource;
    private bool _isPromoFlipAnimating;
    private bool _adminHotspotHoldCompleted;
    private string _enteredAdminPasscode = string.Empty;
    private string _newAdminPasscode = string.Empty;
    private AdminPromptMode _adminPromptMode = AdminPromptMode.Unlock;

    private DashboardViewModel ViewModel => (DashboardViewModel)BindingContext;

    public MainPage()
    {
        InitializeComponent();
        BindingContext = ServiceRegistry.GetRequiredService<DashboardViewModel>();
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await ViewModel.StartAsync();
    }

    protected override async void OnDisappearing()
    {
        CancelAdminHotspotHold();
        HideAdminPrompt();
        await ViewModel.StopAsync();
        base.OnDisappearing();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DashboardViewModel.CurrentPromoIndex))
        {
            _ = AnimatePromoFlipAsync();
        }
    }

    private async Task AnimatePromoFlipAsync()
    {
        if (_isPromoFlipAnimating || PromoSlidesCountIsTooSmall())
        {
            return;
        }

        _isPromoFlipAnimating = true;
        try
        {
            await MainThread.InvokeOnMainThreadAsync(async () =>
            {
                PromoCard.InputTransparent = true;
                await PromoCard.RotateYToAsync(90, 260, Easing.CubicIn);
                PromoCard.Opacity = 0.92;
                PromoCard.RotationY = -90;
                await PromoCard.RotateYToAsync(0, 320, Easing.CubicOut);
                PromoCard.Opacity = 1;
                PromoCard.InputTransparent = false;
            });
        }
        finally
        {
            _isPromoFlipAnimating = false;
        }
    }

    private bool PromoSlidesCountIsTooSmall() => ViewModel.PromoSlides.Count <= 1;

    private void OnAdminHotspotPressed(object? sender, EventArgs e)
    {
        CancelAdminHotspotHold();
        _adminHotspotHoldCompleted = false;
        ShowAdminHotspotCue(AdminHotspotActiveColor);
        _adminHotspotHoldCancellationTokenSource = new CancellationTokenSource();
        _ = WaitForAdminHotspotHoldAsync(_adminHotspotHoldCancellationTokenSource.Token);
    }

    private void OnAdminHotspotReleased(object? sender, EventArgs e)
    {
        if (!_adminHotspotHoldCompleted)
        {
            CancelAdminHotspotHold();
        }
    }

    private void OnAdminHotspotClicked(object? sender, EventArgs e)
    {
    }

    private async Task WaitForAdminHotspotHoldAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(AdminHotspotHoldDuration, cancellationToken);
            _adminHotspotHoldCompleted = true;
            await MainThread.InvokeOnMainThreadAsync(async () =>
            {
                ShowAdminHotspotCue(AdminHotspotReadyColor);
                await ShowAdminPromptAsync();
            });
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void OnAdminDigitClicked(object? sender, EventArgs e)
    {
        if (sender is not Button button || button.CommandParameter is not string digit)
        {
            return;
        }

        if (_enteredAdminPasscode.Length >= AdminPasscodeMaxLength)
        {
            return;
        }

        _enteredAdminPasscode += digit;
        UpdateAdminPromptDisplay();
    }

    private void OnAdminBackspaceClicked(object? sender, EventArgs e)
    {
        if (_enteredAdminPasscode.Length == 0)
        {
            return;
        }

        _enteredAdminPasscode = _enteredAdminPasscode[..^1];
        UpdateAdminPromptDisplay();
    }

    private void OnAdminClearClicked(object? sender, EventArgs e)
    {
        _enteredAdminPasscode = string.Empty;
        UpdateAdminPromptDisplay();
    }

    private void OnAdminCancelClicked(object? sender, EventArgs e) => HideAdminPrompt();

    private async void OnAdminConfirmClicked(object? sender, EventArgs e)
    {
        if (_adminPromptMode == AdminPromptMode.ForceNewPasscode)
        {
            if (!TryAcceptNewAdminPasscode())
            {
                return;
            }

            _adminPromptMode = AdminPromptMode.ForceConfirmPasscode;
            _enteredAdminPasscode = string.Empty;
            UpdateAdminPromptDisplay();
            return;
        }

        if (_adminPromptMode == AdminPromptMode.ForceConfirmPasscode)
        {
            if (!string.Equals(_enteredAdminPasscode, _newAdminPasscode, StringComparison.Ordinal))
            {
                _enteredAdminPasscode = string.Empty;
                UpdateAdminPromptDisplay(
                    T(nameof(AppLanguageStrings.DashboardAdminChangeMismatch)),
                    GetColorResource("Danger", Colors.IndianRed));
                return;
            }

            await ViewModel.ChangeAdminPasscodeAsync(_newAdminPasscode);
            HideAdminPrompt();
            await Shell.Current.GoToAsync(nameof(SettingsPage));
            return;
        }

        if (await ViewModel.IsDefaultAdminPasscodeActiveAsync())
        {
            BeginForcedAdminPasscodeChange();
            return;
        }

        var isValid = await ViewModel.VerifyAdminPasscodeAsync(_enteredAdminPasscode);
        if (!isValid)
        {
            _enteredAdminPasscode = string.Empty;
            UpdateAdminPromptDisplay(
                T(nameof(AppLanguageStrings.DashboardAdminUnlockInvalid)),
                GetColorResource("Danger", Colors.IndianRed));
            return;
        }

        HideAdminPrompt();
        await Shell.Current.GoToAsync(nameof(SettingsPage));
    }

    private async Task ShowAdminPromptAsync()
    {
        HideAdminHotspotCue();
        _enteredAdminPasscode = string.Empty;
        _newAdminPasscode = string.Empty;
        _adminPromptMode = await ViewModel.IsDefaultAdminPasscodeActiveAsync()
            ? AdminPromptMode.ForceNewPasscode
            : AdminPromptMode.Unlock;
        AdminPromptOverlay.IsVisible = true;
        UpdateAdminPromptDisplay();
    }

    private void HideAdminPrompt()
    {
        HideAdminHotspotCue();
        _enteredAdminPasscode = string.Empty;
        _newAdminPasscode = string.Empty;
        _adminPromptMode = AdminPromptMode.Unlock;
        AdminPromptOverlay.IsVisible = false;
        UpdateAdminPromptDisplay();
    }

    private void UpdateAdminPromptDisplay(string? statusText = null, Color? statusColor = null)
    {
        AdminPasscodeDisplayLabel.Text = _enteredAdminPasscode.Length == 0
            ? "----"
            : new string('*', _enteredAdminPasscode.Length);

        AdminPromptStatusLabel.Text = statusText ?? T(_adminPromptMode switch
        {
            AdminPromptMode.ForceNewPasscode => nameof(AppLanguageStrings.DashboardAdminChangeRequired),
            AdminPromptMode.ForceConfirmPasscode => nameof(AppLanguageStrings.DashboardAdminChangeConfirm),
            _ => nameof(AppLanguageStrings.DashboardAdminUnlockPlaceholder),
        });
        AdminPromptStatusLabel.TextColor = statusColor ?? GetColorResource("MutedInk", Colors.Gray);
    }

    private void BeginForcedAdminPasscodeChange()
    {
        _adminPromptMode = AdminPromptMode.ForceNewPasscode;
        _enteredAdminPasscode = string.Empty;
        _newAdminPasscode = string.Empty;
        UpdateAdminPromptDisplay(
            T(nameof(AppLanguageStrings.DashboardAdminChangeRequired)),
            GetColorResource("AccentAmber", Colors.DarkOrange));
    }

    private bool TryAcceptNewAdminPasscode()
    {
        if (_enteredAdminPasscode.Length < 4)
        {
            _enteredAdminPasscode = string.Empty;
            UpdateAdminPromptDisplay(
                T(nameof(AppLanguageStrings.DashboardAdminChangeTooShort)),
                GetColorResource("Danger", Colors.IndianRed));
            return false;
        }

        if (!_enteredAdminPasscode.All(char.IsDigit))
        {
            _enteredAdminPasscode = string.Empty;
            UpdateAdminPromptDisplay(
                T(nameof(AppLanguageStrings.DashboardAdminChangeDigitsOnly)),
                GetColorResource("Danger", Colors.IndianRed));
            return false;
        }

        if (string.Equals(_enteredAdminPasscode, Vendomat.Controller.Domain.Security.AdminPasscodeHasher.DefaultPasscode, StringComparison.Ordinal))
        {
            _enteredAdminPasscode = string.Empty;
            UpdateAdminPromptDisplay(
                T(nameof(AppLanguageStrings.DashboardAdminDefaultPasscodeBlocked)),
                GetColorResource("Danger", Colors.IndianRed));
            return false;
        }

        _newAdminPasscode = _enteredAdminPasscode;
        return true;
    }

    private void CancelAdminHotspotHold()
    {
        _adminHotspotHoldCancellationTokenSource?.Cancel();
        _adminHotspotHoldCancellationTokenSource?.Dispose();
        _adminHotspotHoldCancellationTokenSource = null;
        _adminHotspotHoldCompleted = false;
        HideAdminHotspotCue();
    }

    private void ShowAdminHotspotCue(Color color)
    {
        AdminHotspotButton.BackgroundColor = color;
        AdminHotspotButton.Opacity = 1;
    }

    private void HideAdminHotspotCue()
    {
        AdminHotspotButton.BackgroundColor = Colors.Transparent;
        AdminHotspotButton.Opacity = 0;
    }

    private static string T(string key) =>
        LanguageService.Current?.GetText(key)
        ?? $"[{key}]";

    private static Color GetColorResource(string resourceKey, Color fallbackColor)
    {
        if (Microsoft.Maui.Controls.Application.Current?.Resources.TryGetValue(resourceKey, out var value) == true
            && value is Color color)
        {
            return color;
        }

        return fallbackColor;
    }

    private enum AdminPromptMode
    {
        Unlock,
        ForceNewPasscode,
        ForceConfirmPasscode,
    }
}
