// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Linkast.App.Logging;
using Linkast.App.Services;
using Linkast.Core.Camera;
using Linkast.Platform.Windows.Camera;
using Linkast.App.ViewModels;
using Linkast.Core;
using Linkast.Core.Audio;
using Linkast.Networking;
using Linkast.Platform.Windows.Audio;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Formatting.Display;

namespace Linkast.App;

public partial class App : Application
{
    private IHost? _host;

    public static string DataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Linkast");

    public static string LogDirectory => Path.Combine(DataDirectory, "logs");

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Directory.CreateDirectory(LogDirectory);

        // Every line is redacted at the sink, in addition to redaction at the source.
        var formatter = new RedactingTextFormatter(
            new MessageTemplateTextFormatter("{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}"),
            Redactor.Shared);
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .Enrich.FromLogContext()
            .WriteTo.File(
                formatter,
                Path.Combine(LogDirectory, "linkast-.log"),
                rollingInterval: RollingInterval.Day,
                fileSizeLimitBytes: 10 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                retainedFileCountLimit: 7)
            .CreateLogger();

        DispatcherUnhandledException += OnDispatcherUnhandledException;

        _host = Host.CreateDefaultBuilder()
            .UseSerilog()
            .ConfigureServices(services =>
            {
                services.AddSingleton<IOutputTransport, RelayOutputTransport>();
                // One capture session per audio device, shared by the stream and the voice-level meter.
                services.AddSingleton<AudioCaptureHub>();
                services.AddSingleton<AudioLevelBus>();
                services.AddSingleton<IAudioInputProvider>(sp => new WasapiAudioInputProvider(sp.GetRequiredService<AudioCaptureHub>()));
                services.AddSingleton<MediaCaptureCameraService>();
                services.AddSingleton<ICameraService>(sp =>
                {
                    var cameras = sp.GetRequiredService<MediaCaptureCameraService>();
                    cameras.StartWatching();
                    return cameras;
                });
                services.AddSingleton<ISegmentationService, Linkast.Effects.SegmentationService>();
                services.AddSingleton<BackgroundImageCache>();
                services.AddSingleton<IFramingService>(sp => new FaceFramingService(sp.GetRequiredService<ICameraService>()));
                services.AddSingleton<IVideoLayerInputProvider>(sp => new CameraLayerPipeInputProvider(
                    sp.GetRequiredService<ICameraService>(),
                    sp.GetRequiredService<ISegmentationService>(),
                    sp.GetRequiredService<BackgroundImageCache>(),
                    sp.GetRequiredService<IFramingService>(),
                    sp.GetRequiredService<AudioLevelBus>()));
                services.AddSingleton<JsonSettingsStore>();
                services.AddSingleton(_ => new DpapiSecretStore());
                services.AddSingleton<UpdateService>();
                services.AddSingleton<MainViewModel>();
                services.AddSingleton<MainWindow>();
            })
            .Build();

        await _host.StartAsync();

        // Apply the saved appearance before the first window exists, so it never flashes light.
        ThemeMode = ThemeOption.For(_host.Services.GetRequiredService<JsonSettingsStore>().Load().Theme).Mode;

        var window = _host.Services.GetRequiredService<MainWindow>();
        MainWindow = window;
        window.Show();
        await _host.Services.GetRequiredService<MainViewModel>().InitializeAsync();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            await _host.StopAsync(TimeSpan.FromSeconds(5));

            // Async disposal: the view model (and the engine it owns) only implement IAsyncDisposable.
            if (_host is IAsyncDisposable asyncHost)
            {
                await asyncHost.DisposeAsync();
            }
            else
            {
                _host.Dispose();
            }
        }

        // NetMQ keeps background threads alive until cleaned up.
        NetMQ.NetMQConfig.Cleanup(block: false);
        await Log.CloseAndFlushAsync();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Unhandled UI exception");
        MessageBox.Show(
            MainWindow,
            "Linkast hit an unexpected error. Details were written to the log folder:\n" + LogDirectory,
            "Linkast",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }
}
