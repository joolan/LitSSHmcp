using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;

namespace LitSSHmcp.App.Controls;

[ContentProperty(nameof(FieldContent))]
public partial class FormRow : UserControl
{
    public FormRow()
    {
        InitializeComponent();
    }

    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(FormRow), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty LabelWidthProperty = DependencyProperty.Register(
        nameof(LabelWidth), typeof(GridLength), typeof(FormRow), new PropertyMetadata(new GridLength(92)));

    public static readonly DependencyProperty LabelVerticalAlignmentProperty = DependencyProperty.Register(
        nameof(LabelVerticalAlignment), typeof(VerticalAlignment), typeof(FormRow),
        new PropertyMetadata(VerticalAlignment.Top));

    public static readonly DependencyProperty IsRequiredProperty = DependencyProperty.Register(
        nameof(IsRequired), typeof(bool), typeof(FormRow), new PropertyMetadata(false));

    public static readonly DependencyProperty HelpTextProperty = DependencyProperty.Register(
        nameof(HelpText), typeof(string), typeof(FormRow), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty ErrorTextProperty = DependencyProperty.Register(
        nameof(ErrorText), typeof(string), typeof(FormRow), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty FieldContentProperty = DependencyProperty.Register(
        nameof(FieldContent), typeof(object), typeof(FormRow), new PropertyMetadata(null));

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public GridLength LabelWidth
    {
        get => (GridLength)GetValue(LabelWidthProperty);
        set => SetValue(LabelWidthProperty, value);
    }

    public VerticalAlignment LabelVerticalAlignment
    {
        get => (VerticalAlignment)GetValue(LabelVerticalAlignmentProperty);
        set => SetValue(LabelVerticalAlignmentProperty, value);
    }

    public bool IsRequired
    {
        get => (bool)GetValue(IsRequiredProperty);
        set => SetValue(IsRequiredProperty, value);
    }

    public string HelpText
    {
        get => (string)GetValue(HelpTextProperty);
        set => SetValue(HelpTextProperty, value);
    }

    public string ErrorText
    {
        get => (string)GetValue(ErrorTextProperty);
        set => SetValue(ErrorTextProperty, value);
    }

    public object? FieldContent
    {
        get => GetValue(FieldContentProperty);
        set => SetValue(FieldContentProperty, value);
    }
}
