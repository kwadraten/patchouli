using System;

namespace Patchouli.UI.Controls.CatWalk;

public sealed class CatAnimationStateMachine
{
    public const int TotalActiveFrames = 5;
    public const int IdleFrameIndex = -1;

    private int _currentFrameIndex;
    private bool _isBusy;
    private bool _isWindowMinimized;
    private bool _isAttached = true;

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (_isBusy == value)
            {
                return;
            }

            _isBusy = value;
            if (value)
            {
                _currentFrameIndex = 0;
            }
        }
    }

    public bool IsWindowMinimized
    {
        get => _isWindowMinimized;
        set => _isWindowMinimized = value;
    }

    public bool IsAttached
    {
        get => _isAttached;
        set => _isAttached = value;
    }

    public bool ShouldAnimate => _isBusy && !_isWindowMinimized && _isAttached;

    public int CurrentFrameIndex
    {
        get => _currentFrameIndex;
        internal set => _currentFrameIndex = value >= 0 ? value % TotalActiveFrames : 0;
    }

    public int DisplayFrameIndex => _isBusy ? _currentFrameIndex : IdleFrameIndex;

    public bool Step()
    {
        if (!ShouldAnimate)
        {
            return false;
        }

        _currentFrameIndex = (_currentFrameIndex + 1) % TotalActiveFrames;
        return true;
    }

    public void Reset()
    {
        _currentFrameIndex = 0;
    }
}
