using System;
using System.Threading.Tasks;
using Avalonia.Platform;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Core.Abstractions.Services.Management;
using ClassIsland.Core.Extensions;
using ClassIsland.Models;
using ClassIsland.Shared;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;

namespace ClassIsland.Services;

public class SplashService: ObservableRecipient, ISplashService
{
    private static readonly Uri EchoCaveAssetUri = new("avares://ClassIsland/Assets/Tellings.txt");
    private readonly ILogger<SplashService> _logger;
    private string[]? _echoCaveTexts;
    private string _splashStatus = "正在启动…";
    private double _currentProgress = 0.0;
    private ISplashProvider? _currentSplashProvider;

    public string SplashStatus
    {
        get => _splashStatus;
        set
        {
            if (value == _splashStatus) return;
            _splashStatus = value;
            OnPropertyChanged();
        }
    }

    public void SetDetailedStatus(string message)
    {
        if (SettingsService.Settings.ShowDetailedStatusOnSplash)
        {
            SplashStatus = message;
        }
    }

    public double CurrentProgress
    {
        get => _currentProgress;
        set
        {
            if (value.Equals(_currentProgress)) return;
            _currentProgress = value;
            OnPropertyChanged();
            ProgressChanged?.Invoke(this, value);
        }
    }

    public event EventHandler<double>? ProgressChanged;

    public event EventHandler? SplashEnded;
    public async Task EndSplash()
    {
        if (_currentSplashProvider != null)
        {
            await _currentSplashProvider.EndSplash();
        }
        _currentSplashProvider = null;
    }

    public async Task StartSplash()
    {
        if (_currentSplashProvider != null)
        {
            throw new InvalidOperationException("启动界面已显示，不能重复显示启动界面。");
        }

        _currentSplashProvider = IAppHost.TryGetService<ISplashProvider>();
        if (_currentSplashProvider != null)
        {
            await _currentSplashProvider.StartSplash();
        }
    }

    private SettingsService SettingsService { get; }

    private static string DefaultText { get; } = "正在启动…";

    public SplashService(SettingsService settingsService, IManagementService managementService,
        ILogger<SplashService> logger)
    {
        SettingsService = settingsService;
        _logger = logger;
        if (managementService.Policy.DisableSplashCustomize)
        {
            SettingsService.Settings.SplashCustomLogoSource = "";
            SettingsService.Settings.SplashTextSource = SplashTextSourceType.Custom;
            SettingsService.Settings.SplashCustomText = "";
            SettingsService.Settings.ShowDetailedStatusOnSplash = false;
        }
        ResetSplashText();
    }

    public void ResetSplashText()
    {
        SplashStatus = SettingsService.Settings.SplashTextSource switch
        {
            SplashTextSourceType.EchoCave => GetRandomEchoCaveText(),
            _ => SettingsService.Settings.SplashCustomText == ""
                ? DefaultText
                : SettingsService.Settings.SplashCustomText
        };
    }

    private string GetRandomEchoCaveText()
    {
        if (_echoCaveTexts == null)
        {
            try
            {
                _echoCaveTexts = AssetLoader.ReadAllText(EchoCaveAssetUri)
                    .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "读取回声洞文本失败，将使用默认启动文字");
                _echoCaveTexts = [];
            }
        }

        return _echoCaveTexts.Length > 0
            ? _echoCaveTexts[Random.Shared.Next(_echoCaveTexts.Length)]
            : DefaultText;
    }
}