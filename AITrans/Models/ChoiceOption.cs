using CommunityToolkit.Mvvm.ComponentModel;

namespace AITrans.Models;

/// <summary>One option in a multiple-choice quiz question.</summary>
public partial class ChoiceOption : ObservableObject
{
    public string Text { get; init; } = "";
    public bool IsCorrectAnswer { get; init; }

    [ObservableProperty]
    private ChoiceState _state = ChoiceState.Idle;

    partial void OnStateChanged(ChoiceState value)
    {
        OnPropertyChanged(nameof(IsCorrectState));
        OnPropertyChanged(nameof(IsWrongState));
    }

    public bool IsCorrectState => State == ChoiceState.Correct;
    public bool IsWrongState   => State == ChoiceState.Wrong;
}

public enum ChoiceState
{
    Idle,
    Correct,
    Wrong
}
