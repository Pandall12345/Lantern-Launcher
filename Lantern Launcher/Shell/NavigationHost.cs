using System.Collections.Generic;
using LanternLauncher.Common;

namespace LanternLauncher.Shell;

/// <summary>
/// Navigation stack. Only this type mutates it; the animation layer just displays Current.
/// </summary>
public sealed class NavigationHost : ViewModelBase
{
    private readonly List<Route> _back = new();
    private readonly List<Route> _forward = new();

    private Route _current = new(PageKey.Home);
    public Route Current
    {
        get => _current;
        private set
        {
            if (_current == value) return;
            _current = value;
            OnPropertyChanged();
        }
    }

    public bool CanGoBack => _back.Count > 0;
    public bool CanGoForward => _forward.Count > 0;

    public void Navigate(Route route)
    {
        _back.Add(Current);
        _forward.Clear();
        Current = route;
        NotifyStacks();
    }

    public bool GoBack()
    {
        if (_back.Count == 0) return false;
        _forward.Insert(0, Current);
        Current = _back[^1];
        _back.RemoveAt(_back.Count - 1);
        NotifyStacks();
        return true;
    }

    public bool GoForward()
    {
        if (_forward.Count == 0) return false;
        _back.Add(Current);
        Current = _forward[0];
        _forward.RemoveAt(0);
        NotifyStacks();
        return true;
    }

    private void NotifyStacks()
    {
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
    }
}
