using Microsoft.VisualStudio.TestTools.UnitTesting;
using MyProxy.Core;

namespace MyProxy.Tests;

[TestClass]
public sealed class PairingCodeNormalizerTests
{
    [TestMethod]
    public void Normalize_ThreeSpecifiedForms_ReturnSameCode()
    {
        Assert.AreEqual("A7K9-M2QF", PairingCodeNormalizer.Normalize("A7K9M2QF"));
        Assert.AreEqual("A7K9-M2QF", PairingCodeNormalizer.Normalize("a7k9 m2qf"));
        Assert.AreEqual("A7K9-M2QF", PairingCodeNormalizer.Normalize("A7K9-M2QF"));
    }

    [TestMethod]
    public void Normalize_WhitespaceAndMixedCase_AreHandled()
    {
        Assert.AreEqual("A7K9-M2QF", PairingCodeNormalizer.Normalize("  a7k9 m2qf  "));
        Assert.AreEqual("A7K9-M2QF-A7K9", PairingCodeNormalizer.Normalize("a7k9-m2qfa7k9"));
        Assert.AreEqual("A7K9-M2QF", PairingCodeNormalizer.Normalize("A 7 K 9 M 2 Q F"));
    }

    [TestMethod]
    public void Normalize_NullOrWhiteSpace_ReturnsEmpty()
    {
        Assert.AreEqual("", PairingCodeNormalizer.Normalize(null));
        Assert.AreEqual("", PairingCodeNormalizer.Normalize("   "));
    }

    [TestMethod]
    public void TryNormalize_RequiresExactlyEightAsciiLettersOrDigits()
    {
        Assert.IsTrue(PairingCodeNormalizer.TryNormalize("a7k9 m2qf", out string normalized));
        Assert.AreEqual("A7K9-M2QF", normalized);
        Assert.IsFalse(PairingCodeNormalizer.TryNormalize("A7K9-M2Q", out _));
        Assert.IsFalse(PairingCodeNormalizer.TryNormalize("A7K9-M2QF-X", out _));
        Assert.IsFalse(PairingCodeNormalizer.TryNormalize("A7K9-M2_!", out _));
    }

    [TestMethod]
    public void Format_WhenFifthCharacterInsertsDash_MovesCaretAfterCharacter()
    {
        (string text, int caret) = PairingCodeFormatter.Format("A7K9M", 5);

        Assert.AreEqual("A7K9-M", text);
        Assert.AreEqual(6, caret);

        (text, caret) = PairingCodeFormatter.Format(text.Insert(caret, "2"), caret + 1);
        Assert.AreEqual("A7K9-M2", text);
        Assert.AreEqual(7, caret);
    }
}
