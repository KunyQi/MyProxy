using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using MyProxy.Core;
using TextBox = System.Windows.Controls.TextBox;
using DataObject = System.Windows.DataObject;
using DataFormats = System.Windows.DataFormats;

namespace MyProxy.Views;

internal sealed class PairingCodeInput
{
    private readonly TextBox _box;
    private DispatcherOperation? _pending;
    private bool _formatting;

    internal PairingCodeInput(TextBox box)
    {
        _box = box;
        box.TextChanged += OnTextChanged;
        box.PreviewKeyDown += (_, e) => PrepareDeletion(e.Key);
        DataObject.AddPastingHandler(box, OnPasting);
    }

    private void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_formatting || _pending?.Status == DispatcherOperationStatus.Pending)
        {
            return;
        }

        // TextChanged runs inside WPF's edit transaction, before its final
        // selection update. Replacing Text there can select the entire input,
        // causing the next keystroke to replace all preceding characters.
        _pending = _box.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(FormatAfterEdit));
    }

    private void FormatAfterEdit()
    {
        string original = _box.Text;
        (string formatted, int start) = PairingCodeFormatter.Format(original, _box.SelectionStart);
        (_, int end) = PairingCodeFormatter.Format(original, _box.SelectionStart + _box.SelectionLength);
        if (string.Equals(original, formatted, StringComparison.Ordinal))
        {
            return;
        }

        _formatting = true;
        try
        {
            // Preserve the TwoWay binding and the now-settled caret/selection.
            _box.SetCurrentValue(TextBox.TextProperty, formatted);
            _box.Select(start, end - start);
        }
        finally
        {
            _formatting = false;
        }
    }

    internal void PrepareDeletion(Key key)
    {
        if (_box.SelectionLength != 0 || _box.Text.Length <= 4 || _box.Text[4] != '-')
        {
            return;
        }

        // The separator is formatting, not a character the user must delete twice.
        if (key == Key.Back && _box.CaretIndex == 5)
        {
            _box.Select(3, 2);
        }
        else if (key == Key.Delete && _box.CaretIndex == 4 && _box.Text.Length > 5)
        {
            _box.Select(4, 2);
        }
    }

    private void OnPasting(object sender, DataObjectPastingEventArgs e)
    {
        if (e.SourceDataObject.GetData(DataFormats.UnicodeText) is not string text)
        {
            return;
        }

        // Clean copied separators before TextBox applies MaxLength, otherwise
        // leading spaces can consume the limit and drop the end of a valid code.
        string compact = string.Concat(text.Where(c => !char.IsWhiteSpace(c) && c != '-'));
        e.DataObject = new DataObject(DataFormats.UnicodeText, compact);
        e.FormatToApply = DataFormats.UnicodeText;
    }
}
