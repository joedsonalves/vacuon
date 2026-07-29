using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Vacuon.Core.Optimization;

public enum SwitchOutcome
{
    Applied,
    /// <summary>Already in the requested state. Nothing was written.</summary>
    NoChange,
    /// <summary>Vacuon only reports this one; there is no documented switch to throw.</summary>
    NotActionable,
    /// <summary>The key needs Administrator and this process does not have it.</summary>
    NeedsElevation,
    /// <summary>The write went through but reading it back did not return what was written.</summary>
    NotConfirmed,
    Failed,
}

public sealed record SwitchResult(
    string ComponentId,
    SwitchOutcome Outcome,
    int? PreviousValue = null,
    int? WrittenValue = null,
    string? Message = null)
{
    public bool Succeeded => Outcome is SwitchOutcome.Applied or SwitchOutcome.NoChange;
}

/// <summary>
/// Turns a component off, and puts it back.
/// <para>
/// This is the first code in Vacuon that writes to the registry, and it is deliberately kept
/// away from the Security tab, which promises in the interface and in the CLI that it changed
/// no key. That promise stays true because this lives somewhere else.
/// </para>
/// <para>
/// Every write is journalled first and read back after. Vacuon can honestly say it wrote the
/// documented value and that the value is there — it cannot say Windows will honour it
/// immediately, and the interface does not pretend otherwise.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AiComponentSwitch(PolicyJournal? journal = null)
{
    private readonly PolicyJournal _journal = journal ?? new PolicyJournal();

    public SwitchResult TurnOff(AiComponent component) => Write(component, component.OffValue);

    /// <summary>
    /// Puts a component back the way it was found.
    /// <para>
    /// Restores from the journal rather than writing the "on" value, because those are not the
    /// same thing: a value Vacuon created must be deleted, not set to zero.
    /// </para>
    /// </summary>
    public SwitchResult Undo(AiComponent component)
    {
        if (!component.IsActionable)
            return new SwitchResult(component.Id, SwitchOutcome.NotActionable);

        PolicyChange? change = _journal.LastFor(component.Id);

        // Never touched by Vacuon: the honest answer is to leave it alone rather than invent
        // a previous state.
        if (change is null)
            return new SwitchResult(component.Id, SwitchOutcome.NoChange);

        if (component.NeedsElevation && !IsElevated())
            return new SwitchResult(component.Id, SwitchOutcome.NeedsElevation);

        try
        {
            using RegistryKey root = RegistryKey.OpenBaseKey(component.Hive!.Value, RegistryView.Registry64);

            if (change.PreviousValue is null)
            {
                using (RegistryKey? key = root.OpenSubKey(component.SubKey!, writable: true))
                {
                    key?.DeleteValue(component.ValueName!, throwOnMissingValue: false);
                }

                // Only remove the key when Vacuon created it and left it empty. Deleting a key
                // that was already there could take somebody else's settings with it.
                if (change.KeyCreated) DeleteKeyIfEmpty(root, component.SubKey!);
            }
            else
            {
                using RegistryKey? key = root.OpenSubKey(component.SubKey!, writable: true);
                key?.SetValue(component.ValueName!, change.PreviousValue.Value, RegistryValueKind.DWord);
            }

            _journal.RemoveLast(component.Id);
            return new SwitchResult(component.Id, SwitchOutcome.Applied, null, change.PreviousValue);
        }
        catch (UnauthorizedAccessException ex)
        {
            return new SwitchResult(component.Id, SwitchOutcome.NeedsElevation, Message: ex.Message);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException)
        {
            return new SwitchResult(component.Id, SwitchOutcome.Failed, Message: ex.Message);
        }
    }

    private SwitchResult Write(AiComponent component, int value)
    {
        if (!component.IsActionable)
            return new SwitchResult(component.Id, SwitchOutcome.NotActionable);

        if (component.NeedsElevation && !IsElevated())
            return new SwitchResult(component.Id, SwitchOutcome.NeedsElevation);

        try
        {
            using RegistryKey root = RegistryKey.OpenBaseKey(component.Hive!.Value, RegistryView.Registry64);

            int? previous;
            bool keyCreated;

            using (RegistryKey? existing = root.OpenSubKey(component.SubKey!))
            {
                keyCreated = existing is null;
                previous = existing?.GetValue(component.ValueName!) as int?;
            }

            if (previous == value)
                return new SwitchResult(component.Id, SwitchOutcome.NoChange, previous, value);

            // Journal first. A crash between these two lines has to leave a note about a
            // change that did not happen, never a change nobody recorded.
            _journal.Append(new PolicyChange
            {
                ComponentId = component.Id,
                Hive = component.Hive!.Value.ToString(),
                SubKey = component.SubKey!,
                ValueName = component.ValueName!,
                PreviousValue = previous,
                KeyCreated = keyCreated,
                WrittenValue = value,
                AtUtc = DateTime.UtcNow,
            });

            using (RegistryKey key = root.CreateSubKey(component.SubKey!, writable: true))
            {
                key.SetValue(component.ValueName!, value, RegistryValueKind.DWord);
            }

            // Read it back. "I wrote it" and "it is there" are different claims, and only the
            // second one is worth showing somebody.
            using (RegistryKey? verify = root.OpenSubKey(component.SubKey!))
            {
                if (verify?.GetValue(component.ValueName!) as int? != value)
                    return new SwitchResult(component.Id, SwitchOutcome.NotConfirmed, previous, value);
            }

            return new SwitchResult(component.Id, SwitchOutcome.Applied, previous, value);
        }
        catch (UnauthorizedAccessException ex)
        {
            return new SwitchResult(component.Id, SwitchOutcome.NeedsElevation, Message: ex.Message);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException)
        {
            return new SwitchResult(component.Id, SwitchOutcome.Failed, Message: ex.Message);
        }
    }

    private static void DeleteKeyIfEmpty(RegistryKey root, string subKey)
    {
        try
        {
            using (RegistryKey? key = root.OpenSubKey(subKey))
            {
                if (key is null) return;
                if (key.ValueCount > 0 || key.SubKeyCount > 0) return;
            }

            root.DeleteSubKey(subKey, throwOnMissingSubKey: false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
        }
    }

    private static bool IsElevated()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(identity)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
