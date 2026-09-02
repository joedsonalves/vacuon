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
    private EventHandler? _asked;

    /// <summary>
    /// ⚠️ <b>Subscribes to <see cref="CommandManager.RequerySuggested"/>, and that is not
    /// decoration.</b> Without it WPF asks <see cref="CanExecute"/> once, when the binding is
    /// made, and never again unless somebody remembers to call
    /// <see cref="RaiseCanExecuteChanged"/>. A button whose condition becomes true later just
    /// stays disabled — and a disabled ghost button looks exactly like an enabled one that is
    /// being clicked and ignored.
    /// <para>
    /// Found the hard way: the Edit button in the preview rendered, took the click, and did
    /// nothing, because its condition turned true after a file was selected. Every command in
    /// this app with a condition that changes over time had the same hole.
    /// </para>
    /// </summary>
    public event EventHandler? CanExecuteChanged
    {
        add
        {
            CommandManager.RequerySuggested += value;
            _asked += value;
        }
        remove
        {
            CommandManager.RequerySuggested -= value;
            _asked -= value;
        }
    }

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute is null ? null : _ => canExecute()) { }

    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => execute(parameter);

    /// <summary>For the cases that cannot wait for the next requery.</summary>
    public void RaiseCanExecuteChanged()
    {
        _asked?.Invoke(this, EventArgs.Empty);
        CommandManager.InvalidateRequerySuggested();
    }
}
