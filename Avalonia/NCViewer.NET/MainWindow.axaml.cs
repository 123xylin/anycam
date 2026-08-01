using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AnyCAD.Foundation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using NcViewerNet.Models;
using NcViewerNet.Services;
using NcViewerNet.Visualization;

namespace NcViewerNet;

public sealed partial class MainWindow : Window
{
    private NcDocument? _document;
    private ToolAnimator? _animator;
    private bool _viewerReady;

    public MainWindow()
    {
        InitializeComponent();

        RenderView.SetAnimationCallback((ViewerListener.AnimationHandler)OnAnimationTick);
        SpeedSlider.PropertyChanged += (_, args) =>
        {
            if (args.Property == Slider.ValueProperty && _animator is not null)
                _animator.Speed = SpeedSlider.Value;
        };
    }

    private void RenderView_ViewerReady()
    {
        _viewerReady = true;
        RenderView.ShowCoordinateGrid(true);
        RenderView.EnableAnimation(true);
        RenderView.RequestDraw(EnumUpdateFlags.Camera);
    }

    private async void ImportButton_Click(object? sender, RoutedEventArgs e)
    {
        await ImportFileAsync();
    }

    private async void SampleButton_Click(object? sender, RoutedEventArgs e)
    {
        var sample = FindSampleNc();
        if (sample is null)
        {
            FileNameText.Text = "未找到示例文件 (NCViewer/Data/1200X2400.nc)";
            return;
        }

        await LoadNcAsync(sample);
    }

    private void StartButton_Click(object? sender, RoutedEventArgs e)
    {
        _animator?.Play();
    }

    private void PauseButton_Click(object? sender, RoutedEventArgs e)
    {
        _animator?.Pause();
    }

    private void StopButton_Click(object? sender, RoutedEventArgs e)
    {
        _animator?.Reset();
        RenderView.RequestDraw(EnumUpdateFlags.Scene);
        ProgressBar.Value = 0;
        ProgressText.Text = "0.0%";
    }

    private async Task ImportFileAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "导入 NC 代码",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("NC 代码")
                {
                    Patterns = ["*.nc", "*.cnc", "*.gcode", "*.gc", "*.tap", "*.txt"]
                },
                FilePickerFileTypes.All
            ]
        });

        var file = files.FirstOrDefault();
        if (file is null)
            return;

        var path = file.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path))
            return;

        await LoadNcAsync(path);
    }

    private async Task LoadNcAsync(string path)
    {
        if (!_viewerReady)
            return;

        try
        {
            FileNameText.Text = "解析中…";
            ToolInfoText.Text = string.Empty;

            var document = await Task.Run(() => NcFileParser.Load(path));
            _document = document;

            var scene = NcSceneBuilder.Build(RenderView, document);
            _animator = new ToolAnimator(scene)
            {
                Speed = SpeedSlider.Value
            };

            FileNameText.Text = System.IO.Path.GetFileName(path);
            ToolInfoText.Text = $"T{document.ToolNumber} {document.ToolName}  Ø{document.ToolDiameter:0.##} mm"
                + $"\n段数 {document.Segments.Count:N0}  行程 {document.TotalLength / 1000:0.##} m"
                + $"\n范围 {document.MaxX - document.MinX:0.#} × {document.MaxY - document.MinY:0.#} mm";
            ProgressBar.Value = 0;
            ProgressText.Text = "0.0%";
            RenderView.RequestDraw(EnumUpdateFlags.Scene);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException)
        {
            _animator = null;
            _document = null;
            RenderView.ClearAll();
            FileNameText.Text = ex.Message;
            ToolInfoText.Text = string.Empty;
        }
    }

    private void OnAnimationTick(float timer)
    {
        if (_animator is null)
            return;

        if (_animator.Tick(timer))
        {
            RenderView.RequestDraw(EnumUpdateFlags.Scene);
            ProgressBar.Value = _animator.Progress * 100;
            ProgressText.Text = $"{_animator.Progress * 100:0.0}%";
        }
    }

    private static string? FindSampleNc()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8; i++)
        {
            var candidate = System.IO.Path.Combine(dir, "NCViewer", "Data", "1200X2400.nc");
            if (File.Exists(candidate))
                return candidate;

            var parent = System.IO.Path.GetDirectoryName(dir);
            if (parent is null || parent == dir)
                break;
            dir = parent;
        }
        return null;
    }
}
