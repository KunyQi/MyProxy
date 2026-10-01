using System.ComponentModel;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MyProxy.Views;

namespace MyProxy.Tests;

[TestClass]
public sealed class PairingCodeInputTests
{
    [TestMethod]
    public void ConsecutiveInput_PreservesCharactersCaretAndBinding() => RunSta(() =>
    {
        var model = new InputModel();
        var box = new TextBox { MaxLength = 9 };
        box.SetBinding(TextBox.TextProperty, new Binding(nameof(InputModel.Code))
        {
            Source = model, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
        });
        _ = new PairingCodeInput(box);
        Pump();

        foreach (char c in "abcd1234")
        {
            string before = box.Text;
            int insertion = box.SelectionStart;
            box.SelectedText = c.ToString();
            // The formatter must leave the native edit alone until WPF finishes
            // placing the caret. This is the regression missed by pure string tests.
            Assert.AreEqual(before.Insert(insertion, c.ToString()), box.Text);
            box.Select(insertion + 1, 0);
            Pump();
            Assert.AreEqual(box.Text.Length, box.CaretIndex);
            Assert.AreEqual(0, box.SelectionLength);
            Assert.AreEqual(box.Text, model.Code);
        }

        Assert.AreEqual("ABCD-1234", box.Text);
        Assert.IsTrue(BindingOperations.IsDataBound(box, TextBox.TextProperty));
        model.Code = "WXYZ-9876";
        Pump();
        Assert.AreEqual("WXYZ-9876", box.Text);
    });

    [TestMethod]
    public void Formatting_PreservesSelectionForReplacement() => RunSta(() =>
    {
        var box = new TextBox();
        _ = new PairingCodeInput(box);
        box.Text = "abcd1234";
        box.Select(4, 4);
        Pump();
        Assert.AreEqual("ABCD-1234", box.Text);
        ReplaceSelection(box, "5678");
        Assert.AreEqual("ABCD-5678", box.Text);
        Assert.AreEqual(9, box.CaretIndex);
        box.SelectAll();
        ReplaceSelection(box, "wxyz9876");
        Assert.AreEqual("WXYZ-9876", box.Text);
    });

    [TestMethod]
    public void Paste_CleansWhitespaceBeforeLengthLimit() => RunSta(() =>
    {
        var box = new TextBox { MaxLength = 9 };
        _ = new PairingCodeInput(box);
        var data = new DataObject(DataFormats.UnicodeText, " \r\n abcd - 1234 \t");
        var paste = new DataObjectPastingEventArgs(data, false, DataFormats.UnicodeText);
        box.RaiseEvent(paste);
        string cleaned = (string)paste.DataObject.GetData(paste.FormatToApply);
        Assert.AreEqual("abcd1234", cleaned);
        Assert.IsTrue(cleaned.Length <= box.MaxLength);
        ReplaceSelection(box, cleaned);
        Assert.AreEqual("ABCD-1234", box.Text);
    });

    [TestMethod]
    public void DeleteAcrossSeparator_RemovesAnActualCodeCharacter() => RunSta(() =>
    {
        var box = new TextBox();
        var input = new PairingCodeInput(box);
        box.Text = "ABCD-1234";
        Pump();
        box.Select(5, 0);
        input.PrepareDeletion(Key.Back);
        ReplaceSelection(box, "");
        Assert.AreEqual("ABC1-234", box.Text);
        Assert.AreEqual(3, box.CaretIndex);

        box.Text = "ABCD-1234";
        Pump();
        box.Select(4, 0);
        input.PrepareDeletion(Key.Delete);
        ReplaceSelection(box, "");
        Assert.AreEqual("ABCD-234", box.Text);
        Assert.AreEqual(4, box.CaretIndex);
    });

    private static void ReplaceSelection(TextBox box, string text)
    {
        int start = box.SelectionStart;
        box.SelectedText = text;
        box.Select(start + text.Length, 0);
        Pump();
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(15)), "WPF input test timed out");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    public sealed class InputModel : INotifyPropertyChanged
    {
        private string _code = "";
        public event PropertyChangedEventHandler? PropertyChanged;
        public string Code
        {
            get => _code;
            set { _code = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Code))); }
        }
    }
}
