using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Terminalis.Views;

public partial class ColorPickerDialog : Window
{
    public string? SelectedColor { get; private set; }

    private readonly List<Color> _predefinedColors = new()
    {
        Colors.Black, Colors.DarkGray, Colors.Gray, Colors.LightGray, Colors.White, Colors.Transparent, Colors.Red, Colors.DarkRed,
        Colors.Green, Colors.DarkGreen, Colors.Blue, Colors.DarkBlue, Colors.Yellow, Colors.Orange, Colors.Purple, Colors.Pink,
        Colors.Cyan, Colors.Magenta, Colors.Brown, Colors.Teal, Colors.Navy, Colors.Maroon, Colors.Olive, Colors.Lime
    };

    private bool _isDragging = false;
    private double _hue = 0;
    private double _saturation = 1.0;
    private double _value = 1.0;

    public ColorPickerDialog(string? initialColor)
    {
        InitializeComponent();
        
        PredefinedColorsItemsControl.ItemsSource = _predefinedColors;
        
        ColorGradientBorder.Loaded += ColorGradientBorder_Loaded;
        ColorGradientBorder.SizeChanged += ColorGradientBorder_SizeChanged;
        
        if (!string.IsNullOrEmpty(initialColor))
        {
            try
            {
                var brush = new BrushConverter().ConvertFromString(initialColor) as SolidColorBrush;
                if (brush != null)
                {
                    RedSlider.Value = brush.Color.R;
                    GreenSlider.Value = brush.Color.G;
                    BlueSlider.Value = brush.Color.B;
                    RgbToHsv(brush.Color.R, brush.Color.G, brush.Color.B, out _hue, out _saturation, out _value);
                }
            }
            catch
            {
            }
        }
        
        UpdateGradient();
        UpdateColor();
    }

    private void ColorGradientBorder_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateGradient();
        UpdateIndicatorPosition();
    }

    private void ColorGradientBorder_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateGradient();
        UpdateIndicatorPosition();
    }

    private void UpdateGradient()
    {
        if (ColorGradientCanvas == null || ColorGradientRectangle == null) return;

        var width = (int)(ColorGradientBorder.ActualWidth > 0 ? ColorGradientBorder.ActualWidth : 400);
        var height = (int)(ColorGradientBorder.ActualHeight > 0 ? ColorGradientBorder.ActualHeight : 200);

        if (width <= 0 || height <= 0) return;

        var pixels = new byte[width * height * 4];
        var stride = width * 4;

        for (int y = 0; y < height; y++)
        {
            var value = 1.0 - (y / (double)height);
            for (int x = 0; x < width; x++)
            {
                var hue = (x / (double)width) * 360.0;
                var color = HsvToRgb(hue, 1.0, value);
                
                var index = (y * stride) + (x * 4);
                pixels[index] = color.B;
                pixels[index + 1] = color.G;
                pixels[index + 2] = color.R;
                pixels[index + 3] = 255;
            }
        }

        var bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        bitmap.WritePixels(new Int32Rect(0, 0, width, height), pixels, stride, 0);
        
        var brush = new ImageBrush(bitmap);
        ColorGradientRectangle.Width = width;
        ColorGradientRectangle.Height = height;
        ColorGradientRectangle.Fill = brush;
    }

    private void UpdateIndicatorPosition()
    {
        if (ColorGradientCanvas == null) return;

        var width = ColorGradientBorder.ActualWidth > 0 ? ColorGradientBorder.ActualWidth : 400;
        var height = ColorGradientBorder.ActualHeight > 0 ? ColorGradientBorder.ActualHeight : 200;

        var x = (_hue / 360.0) * width;
        var y = (1.0 - _value) * height;

        Canvas.SetLeft(ColorPickerIndicator, Math.Max(0, Math.Min(width - 12, x - 6)));
        Canvas.SetTop(ColorPickerIndicator, Math.Max(0, Math.Min(height - 12, y - 6)));
    }

    private void ColorGradient_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isDragging = true;
        UpdateColorFromPosition(e.GetPosition(ColorGradientBorder));
        ColorGradientBorder.CaptureMouse();
    }

    private void ColorGradient_MouseMove(object sender, MouseEventArgs e)
    {
        if (_isDragging && e.LeftButton == MouseButtonState.Pressed)
        {
            UpdateColorFromPosition(e.GetPosition(ColorGradientBorder));
        }
    }

    private void ColorGradient_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isDragging)
        {
            _isDragging = false;
            ColorGradientBorder.ReleaseMouseCapture();
        }
    }

    private void UpdateColorFromPosition(Point position)
    {
        var width = ColorGradientBorder.ActualWidth > 0 ? ColorGradientBorder.ActualWidth : 400;
        var height = ColorGradientBorder.ActualHeight > 0 ? ColorGradientBorder.ActualHeight : 200;

        var x = Math.Max(0, Math.Min(width, position.X));
        var y = Math.Max(0, Math.Min(height, position.Y));

        _hue = (x / width) * 360.0;
        _value = 1.0 - (y / height);
        _saturation = 1.0;

        var color = HsvToRgb(_hue, _saturation, _value);
        RedSlider.Value = color.R;
        GreenSlider.Value = color.G;
        BlueSlider.Value = color.B;

        UpdateIndicatorPosition();
        UpdateColor();
        UpdateTextBoxes();
    }

    private Color HsvToRgb(double h, double s, double v)
    {
        int hi = (int)(h / 60) % 6;
        double f = (h / 60) - hi;
        double p = v * (1 - s);
        double q = v * (1 - f * s);
        double t = v * (1 - (1 - f) * s);

        double r, g, b;
        switch (hi)
        {
            case 0: r = v; g = t; b = p; break;
            case 1: r = q; g = v; b = p; break;
            case 2: r = p; g = v; b = t; break;
            case 3: r = p; g = q; b = v; break;
            case 4: r = t; g = p; b = v; break;
            default: r = v; g = p; b = q; break;
        }

        return Color.FromRgb((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
    }

    private void RgbToHsv(byte r, byte g, byte b, out double h, out double s, out double v)
    {
        double rd = r / 255.0;
        double gd = g / 255.0;
        double bd = b / 255.0;

        double max = Math.Max(rd, Math.Max(gd, bd));
        double min = Math.Min(rd, Math.Min(gd, bd));
        double delta = max - min;

        v = max;
        s = max == 0 ? 0 : delta / max;

        if (delta == 0)
        {
            h = 0;
        }
        else if (max == rd)
        {
            h = 60 * (((gd - bd) / delta) % 6);
        }
        else if (max == gd)
        {
            h = 60 * (((bd - rd) / delta) + 2);
        }
        else
        {
            h = 60 * (((rd - gd) / delta) + 4);
        }

        if (h < 0) h += 360;
    }

    private void Slider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        var color = Color.FromRgb(
            (byte)RedSlider.Value,
            (byte)GreenSlider.Value,
            (byte)BlueSlider.Value);
        
        RgbToHsv(color.R, color.G, color.B, out _hue, out _saturation, out _value);
        UpdateIndicatorPosition();
        UpdateColor();
        UpdateTextBoxes();
    }

    private void UpdateColor()
    {
        var color = Color.FromRgb(
            (byte)RedSlider.Value,
            (byte)GreenSlider.Value,
            (byte)BlueSlider.Value);
        
        ColorPreview.Background = new SolidColorBrush(color);
        SelectedColor = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        HexTextBox.Text = SelectedColor;
        
        var brightness = (color.R * 0.299 + color.G * 0.587 + color.B * 0.114);
        HexTextBox.Foreground = brightness > 128 ? Brushes.Black : Brushes.White;
    }

    private void ColorSwatch_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border border && border.Tag is Color color)
        {
            RedSlider.Value = color.R;
            GreenSlider.Value = color.G;
            BlueSlider.Value = color.B;
            RgbToHsv(color.R, color.G, color.B, out _hue, out _saturation, out _value);
            UpdateIndicatorPosition();
            UpdateColor();
            UpdateTextBoxes();
        }
    }

    private void UpdateTextBoxes()
    {
        RedTextBox.Text = ((int)RedSlider.Value).ToString();
        GreenTextBox.Text = ((int)GreenSlider.Value).ToString();
        BlueTextBox.Text = ((int)BlueSlider.Value).ToString();
    }

    private void RedTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (int.TryParse(RedTextBox.Text, out int value) && value >= 0 && value <= 255)
        {
            RedSlider.Value = value;
        }
    }

    private void GreenTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (int.TryParse(GreenTextBox.Text, out int value) && value >= 0 && value <= 255)
        {
            GreenSlider.Value = value;
        }
    }

    private void BlueTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (int.TryParse(BlueTextBox.Text, out int value) && value >= 0 && value <= 255)
        {
            BlueSlider.Value = value;
        }
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}

