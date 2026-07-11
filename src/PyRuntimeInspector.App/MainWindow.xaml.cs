using System.Windows;
using System.Windows.Input;
using PyRuntimeInspector.App.ViewModels;

namespace PyRuntimeInspector.App;

public partial class MainWindow : Window
{
    private bool _panning;
    private Point _panOrigin;
    private double _horizontalOrigin;
    private double _verticalOrigin;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();
        ViewModel.LaunchOutput.CollectionChanged += LaunchOutput_CollectionChanged;
    }

    private MainViewModel ViewModel => (MainViewModel)DataContext;

    private async void RuntimeTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        await ViewModel.SelectTreeNodeAsync(e.NewValue as RuntimeTreeNode);
    }

    private async void PreviewImage_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (PreviewImage.ActualWidth <= 0 || PreviewImage.ActualHeight <= 0)
            return;
        var point = e.GetPosition(PreviewImage);
        var x = (int)(point.X / PreviewImage.ActualWidth * ViewModel.PreviewWidth);
        var y = (int)(point.Y / PreviewImage.ActualHeight * ViewModel.PreviewHeight);
        await ViewModel.LoadPixelAsync(x, y);
    }

    private void PreviewImage_MouseMove(object sender, MouseEventArgs e)
    {
        if (PreviewImage.ActualWidth <= 0 || PreviewImage.ActualHeight <= 0)
            return;
        var point = e.GetPosition(PreviewImage);
        ViewModel.UpdateCursor(
            (int)(point.X / PreviewImage.ActualWidth * ViewModel.PreviewWidth),
            (int)(point.Y / PreviewImage.ActualHeight * ViewModel.PreviewHeight));
    }

    private void ImageViewerScroll_SizeChanged(object sender, SizeChangedEventArgs e) =>
        ViewModel.UpdateFitViewport(e.NewSize.Width, e.NewSize.Height);

    private void ImageViewerScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        ViewModel.Zoom *= e.Delta > 0 ? 1.25 : 0.8;
        e.Handled = true;
    }

    private void ImageViewerScroll_RightButtonDown(object sender, MouseButtonEventArgs e)
    {
        _panning = true;
        _panOrigin = e.GetPosition(ImageViewerScroll);
        _horizontalOrigin = ImageViewerScroll.HorizontalOffset;
        _verticalOrigin = ImageViewerScroll.VerticalOffset;
        ImageViewerScroll.CaptureMouse();
        e.Handled = true;
    }

    private void ImageViewerScroll_RightButtonUp(object sender, MouseButtonEventArgs e)
    {
        _panning = false;
        ImageViewerScroll.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void ImageViewerScroll_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_panning)
            return;
        var point = e.GetPosition(ImageViewerScroll);
        ImageViewerScroll.ScrollToHorizontalOffset(_horizontalOrigin - (point.X - _panOrigin.X));
        ImageViewerScroll.ScrollToVerticalOffset(_verticalOrigin - (point.Y - _panOrigin.Y));
        e.Handled = true;
    }

    private void LaunchOutput_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (ViewModel.LaunchOutput.Count > 0)
            LaunchOutputList.ScrollIntoView(ViewModel.LaunchOutput[^1]);
    }

    private async void Window_Closed(object? sender, EventArgs e)
    {
        ViewModel.LaunchOutput.CollectionChanged -= LaunchOutput_CollectionChanged;
        await ViewModel.DisposeAsync();
    }
}
