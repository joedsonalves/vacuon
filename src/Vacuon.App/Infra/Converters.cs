using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Vacuon.Core.Analyzers;
using Vacuon.Core.Security;

namespace Vacuon.App.Infra;

/// <summary>Bytes → texto legível. Usado onde o valor vem cru do núcleo.</summary>
public sealed class BytesConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value switch
        {
            long bytes => Format.Bytes(bytes),
            int bytes => Format.Bytes(bytes),
            _ => "—",
        };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class CountConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value switch
        {
            long n => Format.Count(n),
            int n => Format.Count(n),
            _ => "—",
        };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Nível de suspeita → cor da escala de risco.
/// <para>
/// Resolve a partir dos recursos da aplicação, e não de cores fixas, para que a escala
/// mude junto com o tema — os matizes que funcionam no escuro não passam de contraste
/// sobre branco.
/// </para>
/// </summary>
public sealed class SuspicionBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string key = value is Suspicion level
            ? level switch
            {
                Suspicion.HighlySuspicious => "Risk.Danger",
                Suspicion.Suspicious => "Risk.Warning",
                Suspicion.Notable => "Risk.Notable",
                _ => "Risk.Blocked",
            }
            : "Risk.Blocked";

        return Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class SuspicionLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Suspicion level
            ? level switch
            {
                Suspicion.HighlySuspicious => "MUITO SUSPEITO",
                Suspicion.Suspicious => "SUSPEITO",
                Suspicion.Notable => "ATENÇÃO",
                _ => "NORMAL",
            }
            : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Categoria de arquivo → cor. Mesma paleta que o treemap vai usar no M7.</summary>
public sealed class CategoryBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string key = (value as string) switch
        {
            FileCategories.Video => "Cat.Video",
            FileCategories.Image => "Cat.Image",
            FileCategories.Audio => "Cat.Audio",
            FileCategories.Document => "Cat.Document",
            FileCategories.Archive or FileCategories.Installer or FileCategories.Disk => "Cat.Archive",
            FileCategories.Code or FileCategories.Build => "Cat.Code",
            _ => "Cat.Other",
        };

        return Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Percentual 0..100 → cor da barra: vermelho quando o disco está no limite.</summary>
public sealed class GaugeBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string key = value is double percent && percent >= 90 ? "Gauge.Critical" : "Gauge.Fill";
        return Application.Current?.TryFindResource(key) as Brush ?? Brushes.Orange;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Fração 0..1 → 0..100, para alimentar as barras de proporção.</summary>
public sealed class ShareToPercentConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double share ? Math.Clamp(share * 100.0, 0, 100) : 0.0;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Booleano invertido → Visibility, para "mostre isto quando aquilo NÃO estiver".</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is bool flag && flag ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>String vazia → Collapsed. Evita espaço reservado para texto que não existe.</summary>
public sealed class EmptyStringToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Compara o valor com o parâmetro. Serve para ligar um <c>RadioButton</c> a um enum
/// sem precisar de uma propriedade booleana por opção.
/// </summary>
public sealed class EnumEqualsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value?.ToString() == parameter?.ToString();

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is bool selected && selected && parameter is not null
            ? Enum.Parse(targetType, parameter.ToString()!)
            : Binding.DoNothing;
}
