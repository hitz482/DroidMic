using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;

namespace DroidMic.Desktop.Views;

/// <summary>Keeps a ListView scrolled to the newest item.</summary>
public static class AutoScroll
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(AutoScroll), new PropertyMetadata(false, OnChanged));

    public static bool GetEnabled(DependencyObject o) => (bool)o.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject o, bool value) => o.SetValue(EnabledProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ListView list || e.NewValue is not true) return;
        if (list.Items is INotifyCollectionChanged incc)
        {
            incc.CollectionChanged += (_, args) =>
            {
                if (args.Action == NotifyCollectionChangedAction.Add && list.Items.Count > 0)
                    list.ScrollIntoView(list.Items[list.Items.Count - 1]);
            };
        }
    }
}
