using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace LitSSHmcp.App.Controls;

/// <summary>
/// 区分键盘焦点与鼠标焦点：鼠标点击获得焦点时不显示强调色边框，
/// 只有 Tab/方向键等键盘导航获得焦点时才显示。供模板通过附加属性绑定。
/// </summary>
public static class KeyboardFocusRing
{
    private static bool _lastInputWasKeyboard;

    public static readonly DependencyProperty ShowRingProperty = DependencyProperty.RegisterAttached(
        "ShowRing", typeof(bool), typeof(KeyboardFocusRing), new PropertyMetadata(true));

    static KeyboardFocusRing()
    {
        EventManager.RegisterClassHandler(
            typeof(Control),
            Keyboard.GotKeyboardFocusEvent,
            new KeyboardFocusChangedEventHandler(OnGotKeyboardFocus));

        InputManager.Current.PreProcessInput += (_, e) =>
        {
            if (e.StagingItem.Input is KeyEventArgs { IsDown: true })
                _lastInputWasKeyboard = true;
            else if (e.StagingItem.Input is MouseButtonEventArgs { ButtonState: MouseButtonState.Pressed })
                _lastInputWasKeyboard = false;
        };
    }

    public static void SetShowRing(DependencyObject element, bool value)
        => element.SetValue(ShowRingProperty, value);

    public static bool GetShowRing(DependencyObject element)
        => (bool)element.GetValue(ShowRingProperty);

    private static void OnGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is DependencyObject element)
            SetShowRing(element, _lastInputWasKeyboard);
    }
}
