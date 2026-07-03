using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AnyCAD.Foundation;
using AnyTubeNet.Models;
using AnyTubeNet.Services;
using AnyTubeNet.Visualization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace AnyTubeNet;

public sealed partial class MainWindow : Window
{
    private AnyTubeDocument? _document;
    private PathAnimator? _animator;
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
        await ImportJsonAsync();
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
    }

    private async Task ImportJsonAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "导入 AnyTube JSON",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("AnyTube JSON")
                {
                    Patterns = ["*.json"]
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

        LoadJson(path);
    }

    private void LoadJson(string path)
    {
        try
        {
            if (!_viewerReady)
                return;

            _document = AnyTubeJsonLoader.Load(path);
            var scene = AnyTubeSceneBuilder.Build(RenderView, _document);
            _animator = new PathAnimator(scene)
            {
                Speed = SpeedSlider.Value
            };

            FileNameText.Text = System.IO.Path.GetFileName(path);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or InvalidOperationException)
        {
            _animator = null;
            _document = null;
            RenderView.ClearAll();
            FileNameText.Text = ex.Message;
        }
    }

    private void OnAnimationTick(float timer)
    {
        if (_animator?.Tick(timer) == true)
            RenderView.RequestDraw(EnumUpdateFlags.Scene);
    }

}
