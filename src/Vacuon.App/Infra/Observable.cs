using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace Vacuon.App.Infra;

/// <summary>
/// Base de MVVM escrita à mão, ~60 linhas.
/// <para>
/// Escolha deliberada de não trazer o CommunityToolkit.Mvvm: o app usa exatamente
/// <c>Set</c> e <c>RelayCommand</c>, e um gerador de código a mais no build só
/// adicionaria uma dependência e uma fonte de surpresa. Se a superfície de MVVM
/// crescer (mensageria, validação), a troca é local a este arquivo.
/// </para>
/// </summary>
public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
}

public sealed class RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute is null ? null : _ => canExecute()) { }

    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => execute(parameter);

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
