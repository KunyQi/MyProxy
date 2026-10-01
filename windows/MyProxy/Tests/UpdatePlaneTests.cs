using System.IO;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MyProxy.Core;
using MyProxy.Services;

namespace MyProxy.Tests;

/// <summary>
/// 测试用 Ed25519 签名器。
///
/// 它只存在于测试里：产品代码<b>只验不签</b>，私钥路径不进发布包，是
/// 「客户端无法伪造一个它自己会接受的更新」这条性质的根据。
/// </summary>
internal static class TestSigner
{
    private static readonly BigInteger P = BigInteger.Pow(2, 255) - 19;

    private static readonly BigInteger L =
        BigInteger.Pow(2, 252) + BigInteger.Parse("27742317777372353535851937790883648493");

    private static readonly BigInteger D = Mod(-121665 * BigInteger.ModPow(121666, P - 2, P));
    private static readonly BigInteger SqrtMinusOne = BigInteger.ModPow(2, (P - 1) / 4, P);
    private static readonly BigInteger[] Identity = { 0, 1, 1, 0 };
    private static readonly BigInteger[] Base = BuildBase();

    public static byte[] PublicKey(byte[] secret)
    {
        (BigInteger scalar, _) = Expand(secret);
        return Compress(Multiply(scalar, Base));
    }

    public static byte[] Sign(byte[] secret, byte[] message)
    {
        (BigInteger scalar, byte[] prefix) = Expand(secret);
        byte[] encodedA = PublicKey(secret);

        BigInteger r = BigInteger.Remainder(LittleEndian(SHA512.HashData(Concat(prefix, message))), L);
        byte[] encodedR = Compress(Multiply(r, Base));
        BigInteger k = BigInteger.Remainder(
            LittleEndian(SHA512.HashData(Concat(encodedR, encodedA, message))), L);

        BigInteger s = BigInteger.Remainder(r + k * scalar, L);
        byte[] signature = new byte[64];
        encodedR.CopyTo(signature, 0);
        WriteLittleEndian(s, signature.AsSpan(32));
        return signature;
    }

    private static (BigInteger, byte[]) Expand(byte[] secret)
    {
        byte[] digest = SHA512.HashData(secret);
        BigInteger scalar = LittleEndian(digest.AsSpan(0, 32));
        scalar &= (BigInteger.One << 254) - 8;
        scalar |= BigInteger.One << 254;
        return (scalar, digest[32..]);
    }

    private static byte[] Concat(params byte[][] parts)
    {
        byte[] result = new byte[parts.Sum(part => part.Length)];
        int offset = 0;
        foreach (byte[] part in parts)
        {
            part.CopyTo(result, offset);
            offset += part.Length;
        }

        return result;
    }

    private static BigInteger Mod(BigInteger value)
    {
        BigInteger result = BigInteger.Remainder(value, P);
        return result.Sign < 0 ? result + P : result;
    }

    private static BigInteger LittleEndian(ReadOnlySpan<byte> data) =>
        new(data, isUnsigned: true, isBigEndian: false);

    private static void WriteLittleEndian(BigInteger value, Span<byte> destination)
    {
        byte[] raw = value.ToByteArray(isUnsigned: true, isBigEndian: false);
        destination.Clear();
        raw.AsSpan(0, Math.Min(raw.Length, destination.Length)).CopyTo(destination);
    }

    private static BigInteger[] BuildBase()
    {
        BigInteger y = Mod(4 * BigInteger.ModPow(5, P - 2, P));
        BigInteger x = RecoverX(y, 0);
        return new[] { x, y, BigInteger.One, Mod(x * y) };
    }

    private static BigInteger RecoverX(BigInteger y, int sign)
    {
        BigInteger x2 = Mod((y * y - 1) * BigInteger.ModPow(Mod(D * y * y + 1), P - 2, P));
        BigInteger x = BigInteger.ModPow(x2, (P + 3) / 8, P);
        if (!Mod(x * x - x2).IsZero)
        {
            x = Mod(x * SqrtMinusOne);
        }

        if ((int)(x & BigInteger.One) != sign)
        {
            x = P - x;
        }

        return x;
    }

    private static byte[] Compress(BigInteger[] point)
    {
        BigInteger zInverse = BigInteger.ModPow(point[2], P - 2, P);
        BigInteger x = Mod(point[0] * zInverse);
        BigInteger y = Mod(point[1] * zInverse);
        BigInteger encoded = y | ((x & BigInteger.One) << 255);
        byte[] result = new byte[32];
        WriteLittleEndian(encoded, result);
        return result;
    }

    private static BigInteger[] Add(BigInteger[] p, BigInteger[] q)
    {
        BigInteger a = Mod((p[1] - p[0]) * (q[1] - q[0]));
        BigInteger b = Mod((p[1] + p[0]) * (q[1] + q[0]));
        BigInteger c = Mod(2 * p[3] * q[3] * D);
        BigInteger d = Mod(2 * p[2] * q[2]);
        BigInteger e = b - a;
        BigInteger f = d - c;
        BigInteger g = d + c;
        BigInteger h = b + a;
        return new[] { Mod(e * f), Mod(g * h), Mod(f * g), Mod(e * h) };
    }

    private static BigInteger[] Multiply(BigInteger scalar, BigInteger[] point)
    {
        BigInteger[] result = Identity;
        BigInteger[] addend = point;
        while (scalar.Sign > 0)
        {
            if (!(scalar & BigInteger.One).IsZero)
            {
                result = Add(result, addend);
            }

            addend = Add(addend, addend);
            scalar >>= 1;
        }

        return result;
    }
}

[TestClass]
public class Ed25519Tests
{
    private static readonly byte[] RfcSecret = Convert.FromHexString(
        "9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60");

    private static readonly byte[] RfcPublic = Convert.FromHexString(
        "d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a");

    private static readonly byte[] RfcSignature = Convert.FromHexString(
        "e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e06522490155" +
        "5fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b");

    /// <summary>
    /// 与自家签名器对跑也能通过一个算错了但自洽的曲线，所以钉 RFC 8032 的
    /// 公开向量：同一个私钥必须产出与 RFC 一模一样的公钥和签名字节。
    /// 服务端 <c>release.py</c> 钉的是同一组向量，两边因此必然对得上。
    /// </summary>
    [TestMethod]
    public void Rfc8032Vector1_MatchesByteForByte()
    {
        CollectionAssert.AreEqual(RfcPublic, TestSigner.PublicKey(RfcSecret));
        CollectionAssert.AreEqual(RfcSignature, TestSigner.Sign(RfcSecret, Array.Empty<byte>()));
        Assert.IsTrue(Ed25519.Verify(RfcPublic, Array.Empty<byte>(), RfcSignature));
    }

    [TestMethod]
    public void MalformedInputs_ReturnFalseInsteadOfThrowing()
    {
        byte[] secret = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        byte[] other = Enumerable.Range(32, 32).Select(i => (byte)i).ToArray();
        byte[] message = Encoding.UTF8.GetBytes("payload");
        byte[] good = TestSigner.Sign(secret, message);

        Assert.IsFalse(Ed25519.Verify(TestSigner.PublicKey(secret)[..31], message, good), "short key");
        Assert.IsFalse(Ed25519.Verify(TestSigner.PublicKey(secret), message, good[..63]), "short signature");
        Assert.IsFalse(Ed25519.Verify(TestSigner.PublicKey(secret), Encoding.UTF8.GetBytes("other"), good), "wrong message");
        Assert.IsFalse(Ed25519.Verify(TestSigner.PublicKey(other), message, good), "wrong key");

        byte[] flipped = (byte[])good.Clone();
        flipped[0] ^= 1;
        Assert.IsFalse(Ed25519.Verify(TestSigner.PublicKey(secret), message, flipped), "flipped bit");
    }

    [TestMethod]
    public void NonCanonicalS_IsRejected()
    {
        // S >= L 编码的是一个等价标量。做归约而不是拒绝，会让同一个签名对应
        // 两串不同的字节——签名可塑性。
        byte[] secret = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        byte[] message = Encoding.UTF8.GetBytes("payload");
        byte[] good = TestSigner.Sign(secret, message);

        BigInteger l = BigInteger.Pow(2, 252) + BigInteger.Parse("27742317777372353535851937790883648493");
        BigInteger s = new(good.AsSpan(32), isUnsigned: true, isBigEndian: false);
        byte[] mutated = (byte[])good.Clone();
        byte[] raw = (s + l).ToByteArray(isUnsigned: true, isBigEndian: false);
        mutated.AsSpan(32).Clear();
        raw.AsSpan(0, Math.Min(raw.Length, 32)).CopyTo(mutated.AsSpan(32));

        Assert.IsFalse(Ed25519.Verify(TestSigner.PublicKey(secret), message, mutated));
    }
}

[TestClass]
public class ReleaseManifestTests
{
    private static readonly byte[] Secret = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
    private static readonly byte[] OtherSecret = Enumerable.Range(64, 32).Select(i => (byte)i).ToArray();
    private const string KeyId = "test-key";

    private static Dictionary<string, byte[]> Keys() =>
        new(StringComparer.Ordinal) { [KeyId] = TestSigner.PublicKey(Secret) };

    internal static string ManifestJson(
        string version = "1.2.3",
        string platform = "windows",
        string channel = "stable",
        bool mandatory = false,
        string url = "",
        string sha256 = "",
        long size = 4096,
        string signatureType = "authenticode",
        string subject = "",
        string minimumVersion = "",
        int schemaVersion = 1)
    {
        url = url.Length > 0 ? url : $"https://releases.example/MyProxy-{version}.zip";
        sha256 = sha256.Length > 0 ? sha256 : new string('a', 64);
        subject = subject.Length > 0 ? subject : new string('b', 64);

        var document = new Dictionary<string, object?>
        {
            ["schemaVersion"] = schemaVersion,
            ["platform"] = platform,
            ["version"] = version,
            ["channel"] = channel,
            ["mandatory"] = mandatory,
            ["issuedAt"] = "2026-09-20T00:00:00Z",
            ["artifact"] = new Dictionary<string, object?>
            {
                ["url"] = url,
                ["sha256"] = sha256,
                ["size"] = size,
                ["signature"] = new Dictionary<string, object?>
                {
                    ["type"] = signatureType,
                    ["subjectSha256"] = subject
                }
            }
        };

        if (minimumVersion.Length > 0)
        {
            document["minimumVersion"] = minimumVersion;
        }

        return JsonSerializer.Serialize(document);
    }

    internal static (string Manifest, string Signature) Sign(string json, byte[]? secret = null)
    {
        byte[] raw = Encoding.UTF8.GetBytes(json);
        byte[] signed = new byte[ReleaseManifestVerifier.SigningDomain.Length + raw.Length];
        ReleaseManifestVerifier.SigningDomain.CopyTo(signed, 0);
        raw.CopyTo(signed, ReleaseManifestVerifier.SigningDomain.Length);

        return (
            Convert.ToBase64String(raw),
            Convert.ToBase64String(TestSigner.Sign(secret ?? Secret, signed)));
    }

    [TestMethod]
    public void WellFormedManifest_Verifies()
    {
        (string manifest, string signature) = Sign(ManifestJson());
        ManifestVerification result = ReleaseManifestVerifier.Verify(manifest, signature, KeyId, Keys());

        Assert.IsTrue(result.Ok, result.Rejection.ToString());
        Assert.AreEqual("1.2.3", result.Manifest!.Version);
        Assert.AreEqual(4096L, result.Manifest.ArtifactSize);
    }

    [TestMethod]
    public void NoTrustedKeys_FailsClosed()
    {
        (string manifest, string signature) = Sign(ManifestJson());
        ManifestVerification result = ReleaseManifestVerifier.Verify(
            manifest, signature, KeyId, new Dictionary<string, byte[]>());

        Assert.AreEqual(ManifestRejection.UnknownSigningKey, result.Rejection);
    }

    [TestMethod]
    public void SignatureFromAnUntrustedKey_IsRejected()
    {
        (string manifest, string signature) = Sign(ManifestJson(), OtherSecret);
        Assert.AreEqual(
            ManifestRejection.BadSignature,
            ReleaseManifestVerifier.Verify(manifest, signature, KeyId, Keys()).Rejection);
    }

    [TestMethod]
    public void ReserialisedManifest_DoesNotVerify()
    {
        // 这条钉住「按原始字节验签」这个决定：同一份文档换一种序列化写法
        // 就必须验不过，这正是任何一跳都不许重新编码的原因。
        string json = ManifestJson();
        (string manifest, string signature) = Sign(json);

        using JsonDocument parsed = JsonDocument.Parse(json);
        string reserialised = JsonSerializer.Serialize(parsed.RootElement, new JsonSerializerOptions
        {
            WriteIndented = true
        });
        Assert.AreNotEqual(json, reserialised);

        string tampered = Convert.ToBase64String(Encoding.UTF8.GetBytes(reserialised));
        Assert.AreEqual(
            ManifestRejection.BadSignature,
            ReleaseManifestVerifier.Verify(tampered, signature, KeyId, Keys()).Rejection);
        Assert.AreNotEqual("", manifest);
    }

    [TestMethod]
    public void TamperedManifest_IsRejected()
    {
        (string manifest, string signature) = Sign(ManifestJson());
        byte[] raw = Convert.FromBase64String(manifest);
        raw[^2] ^= 0x01;

        Assert.AreEqual(
            ManifestRejection.BadSignature,
            ReleaseManifestVerifier.Verify(
                Convert.ToBase64String(raw), signature, KeyId, Keys()).Rejection);
    }

    [DataTestMethod]
    [DataRow("http://releases.example/a.zip", "", 4096L, "authenticode", "", "linux", ManifestRejection.WrongPlatform)]
    [DataRow("https://u:p@releases.example/a.zip", "", 4096L, "authenticode", "", "windows", ManifestRejection.BadArtifact)]
    [DataRow("", "ZZZZ", 4096L, "authenticode", "", "windows", ManifestRejection.BadArtifact)]
    [DataRow("", "", 0L, "authenticode", "", "windows", ManifestRejection.BadArtifact)]
    [DataRow("", "", 4096L, "apksigner", "", "windows", ManifestRejection.BadArtifact)]
    [DataRow("", "", 4096L, "authenticode", "nothex", "windows", ManifestRejection.BadArtifact)]
    public void StructuralViolations_AreRejected(
        string url, string sha256, long size, string signatureType, string subject, string platform,
        ManifestRejection expected)
    {
        string json = ManifestJson(
            platform: platform,
            url: url,
            sha256: sha256.Length > 0 ? sha256.PadRight(64, 'z')[..64] : "",
            size: size,
            signatureType: signatureType,
            subject: subject.Length > 0 ? subject.PadRight(64, 'z')[..64] : "");
        (string manifest, string signature) = Sign(json);

        Assert.AreEqual(expected, ReleaseManifestVerifier.Verify(manifest, signature, KeyId, Keys()).Rejection);
    }

    [TestMethod]
    public void HttpUrl_IsRejected()
    {
        (string manifest, string signature) = Sign(ManifestJson(url: "http://releases.example/a.zip"));
        Assert.AreEqual(
            ManifestRejection.BadArtifact,
            ReleaseManifestVerifier.Verify(manifest, signature, KeyId, Keys()).Rejection);
    }

    [TestMethod]
    public void UnsupportedSchemaVersion_IsRejected()
    {
        (string manifest, string signature) = Sign(ManifestJson(schemaVersion: 2));
        Assert.AreEqual(
            ManifestRejection.UnsupportedSchema,
            ReleaseManifestVerifier.Verify(manifest, signature, KeyId, Keys()).Rejection);
    }

    [TestMethod]
    public void NonJsonPayload_IsRejectedAfterTheSignatureCheck()
    {
        (string manifest, string signature) = Sign("not json at all");
        Assert.AreEqual(
            ManifestRejection.MalformedDocument,
            ReleaseManifestVerifier.Verify(manifest, signature, KeyId, Keys()).Rejection);
    }

    [TestMethod]
    public void SigningDomain_MatchesTheServer()
    {
        // 这串字节是三端之间唯一的约定；改一个字符就会让所有已签名的 release
        // 在客户端全部验不过。
        Assert.AreEqual(
            "myproxy-release-manifest-v1\0",
            Encoding.ASCII.GetString(ReleaseManifestVerifier.SigningDomain));
    }
}

[TestClass]
public class UpdatePlannerTests
{
    private static ManifestVerification Verified(string version, string minimumVersion = "") =>
        new(ManifestRejection.None, new ReleaseManifest(
            "windows", version, "stable", false,
            "https://releases.example/a.zip", new string('a', 64), 1,
            "authenticode", new string('b', 64), "2026-09-20T00:00:00Z", minimumVersion));

    [TestMethod]
    public void HigherVersion_Installs()
    {
        Assert.AreEqual(UpdateDecision.Install, UpdatePlanner.Decide(Verified("2.0.0"), "1.0.0").Decision);
    }

    [TestMethod]
    public void SameOrLowerVersion_IsUpToDate()
    {
        Assert.AreEqual(UpdateDecision.UpToDate, UpdatePlanner.Decide(Verified("1.0.0"), "1.0.0").Decision);
        Assert.AreEqual(UpdateDecision.UpToDate, UpdatePlanner.Decide(Verified("0.9.0"), "1.0.0").Decision);
    }

    [TestMethod]
    public void RejectedManifest_NeverBecomesUpToDate()
    {
        // 「验签没过」和「已是最新」是两个结论。把前者显示成后者，等于告诉
        // 用户一切正常，而实际上他们正卡在一个装不上的更新前面。
        UpdatePlanResult plan = UpdatePlanner.Decide(
            new ManifestVerification(ManifestRejection.BadSignature), "1.0.0");

        Assert.AreEqual(UpdateDecision.Rejected, plan.Decision);
        Assert.AreEqual(ManifestRejection.BadSignature, plan.Rejection);
    }

    [TestMethod]
    public void UnparseableVersion_IsRejectedNotAssumedCurrent()
    {
        Assert.AreEqual(UpdateDecision.Rejected, UpdatePlanner.Decide(Verified("1.2"), "1.0.0").Decision);
    }

    [TestMethod]
    public void MinimumVersionAboveLocal_RequiresAnUpgradePath()
    {
        // 直接跳装会得到一个升级路径从没被测过的安装。
        UpdatePlanResult plan = UpdatePlanner.Decide(Verified("3.0.0", minimumVersion: "2.0.0"), "1.0.0");
        Assert.AreEqual(UpdateDecision.UpgradePathRequired, plan.Decision);
    }

    [TestMethod]
    public void MinimumVersionSatisfied_Installs()
    {
        Assert.AreEqual(
            UpdateDecision.Install,
            UpdatePlanner.Decide(Verified("3.0.0", minimumVersion: "2.0.0"), "2.1.0").Decision);
    }

    [TestMethod]
    public void PrereleaseSuffix_ParsesToItsNumericCore()
    {
        Assert.IsTrue(ReleaseManifestVerifier.TryParseVersion("1.2.3-beta.1", out Version parsed));
        Assert.AreEqual(new Version(1, 2, 3), parsed);
        Assert.IsFalse(ReleaseManifestVerifier.TryParseVersion("1.2", out _));
        Assert.IsFalse(ReleaseManifestVerifier.TryParseVersion("", out _));
    }
}

[TestClass]
public class UpdateRecoveryTests
{
    private static UpdateJournal Journal(UpdateStage stage, string target = "2.0.0") => new()
    {
        Stage = stage,
        ReleaseId = "rel_x",
        TargetVersion = target,
        PreviousVersion = "1.0.0"
    };

    [TestMethod]
    public void NoJournal_DoesNothing()
    {
        Assert.AreEqual(RecoveryAction.None, UpdateRecovery.Decide(null, "1.0.0"));
    }

    [TestMethod]
    public void Staged_OnlyDiscardsStaging()
    {
        // 安装目录还没被碰过，没有任何东西需要恢复。
        Assert.AreEqual(RecoveryAction.DiscardStaging, UpdateRecovery.Decide(Journal(UpdateStage.Staged), "1.0.0"));
    }

    [TestMethod]
    public void InterruptedSwap_RestoresTheBackup()
    {
        // 此刻安装目录可能已经被移走，只有恢复备份是安全的。
        Assert.AreEqual(RecoveryAction.RestoreBackup, UpdateRecovery.Decide(Journal(UpdateStage.Applying), "1.0.0"));
    }

    [TestMethod]
    public void AppliedAndRunningTheTarget_Finalizes()
    {
        Assert.AreEqual(
            RecoveryAction.FinalizeInstall,
            UpdateRecovery.Decide(Journal(UpdateStage.Applied), "2.0.0"));
    }

    [TestMethod]
    public void AppliedButStillRunningTheOldVersion_RollsBack()
    {
        // 交换完成、跑起来的却不是目标版本：新版本起不来。不能当成功。
        Assert.AreEqual(
            RecoveryAction.RollbackFailedInstall,
            UpdateRecovery.Decide(Journal(UpdateStage.Applied), "1.0.0"));
    }

    [TestMethod]
    public void BuildMetadataDoesNotLookLikeAFailedInstall()
    {
        // 1.2.3 与 1.2.3+build.7 是同一个版本；字符串相等会把一次成功的安装
        // 误判成失败并触发一次没必要的回滚。
        Assert.AreEqual(
            RecoveryAction.FinalizeInstall,
            UpdateRecovery.Decide(Journal(UpdateStage.Applied, "1.2.3"), "1.2.3+build.7"));
    }

    [TestMethod]
    public void RolledBack_OnlyReports()
    {
        Assert.AreEqual(RecoveryAction.ReportRollback, UpdateRecovery.Decide(Journal(UpdateStage.RolledBack), "1.0.0"));
    }

    [DataTestMethod]
    [DataRow(RecoveryAction.FinalizeInstall, "installed")]
    [DataRow(RecoveryAction.RollbackFailedInstall, "failed")]
    [DataRow(RecoveryAction.RestoreBackup, "failed")]
    [DataRow(RecoveryAction.ReportRollback, "rolledback")]
    [DataRow(RecoveryAction.DiscardStaging, "")]
    [DataRow(RecoveryAction.None, "")]
    public void ReportStatus_MapsEveryAction(RecoveryAction action, string expected)
    {
        Assert.AreEqual(expected, UpdateRecovery.ReportStatusFor(action));
    }
}

[TestClass]
public class UpdateApplierTests
{
    private string _root = "";

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "myproxy-apply-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    private (string Install, string Staged, string Backup) Layout(bool withStaged = true)
    {
        string install = Path.Combine(_root, "app");
        string staged = Path.Combine(_root, "staged");
        string backup = Path.Combine(_root, "backup");

        Directory.CreateDirectory(install);
        File.WriteAllText(Path.Combine(install, "marker.txt"), "old");
        WriteLegacyAppFiles(install);
        if (withStaged)
        {
            Directory.CreateDirectory(staged);
            File.WriteAllText(Path.Combine(staged, "marker.txt"), "new");
            WriteLegacyAppFiles(staged);
        }

        return (install, staged, backup);
    }

    private static void WriteLegacyAppFiles(string directory)
    {
        foreach (string name in new[] { "MyProxy.exe", "MyProxy.dll", "MyProxy.deps.json", "MyProxy.runtimeconfig.json" })
        {
            File.WriteAllText(Path.Combine(directory, name), name);
        }
        Directory.CreateDirectory(Path.Combine(directory, "Core"));
        File.WriteAllText(Path.Combine(directory, "Core", "xray.exe"), "core");
    }

    [TestMethod]
    public void Swap_MovesTheNewVersionInAndKeepsTheOldAsBackup()
    {
        (string install, string staged, string backup) = Layout();

        SwapOutcome outcome = UpdateApplier.Swap(install, staged, backup);

        Assert.IsTrue(outcome.Ok, outcome.Detail);
        Assert.AreEqual("new", File.ReadAllText(Path.Combine(install, "marker.txt")));
        // 备份必须还在：新版本还没证明自己能跑起来。
        Assert.AreEqual("old", File.ReadAllText(Path.Combine(backup, "marker.txt")));
    }

    [TestMethod]
    public void Swap_WithoutStaging_LeavesTheInstallUntouched()
    {
        (string install, string staged, string backup) = Layout(withStaged: false);

        SwapOutcome outcome = UpdateApplier.Swap(install, staged, backup);

        Assert.IsFalse(outcome.Ok);
        Assert.IsFalse(outcome.RolledBack);
        Assert.AreEqual("old", File.ReadAllText(Path.Combine(install, "marker.txt")));
    }

    [TestMethod]
    public void Restore_PutsTheBackupBack()
    {
        (string install, string staged, string backup) = Layout();
        UpdateApplier.Swap(install, staged, backup);

        SwapOutcome outcome = UpdateApplier.Restore(install, backup);

        Assert.IsTrue(outcome.Ok, outcome.Detail);
        Assert.IsTrue(outcome.RolledBack);
        Assert.AreEqual("old", File.ReadAllText(Path.Combine(install, "marker.txt")));
        Assert.IsFalse(Directory.Exists(backup));
    }

    [TestMethod]
    public void Restore_WithoutABackup_IsASuccessfulNoOp()
    {
        // 没有备份说明交换根本没走到移动那一步，安装目录还是完整的。
        (string install, _, string backup) = Layout(withStaged: false);

        SwapOutcome outcome = UpdateApplier.Restore(install, backup);

        Assert.IsTrue(outcome.Ok);
        Assert.IsFalse(outcome.RolledBack);
        Assert.AreEqual("old", File.ReadAllText(Path.Combine(install, "marker.txt")));
    }

    [TestMethod]
    public void DiscardBackup_RemovesIt()
    {
        (string install, string staged, string backup) = Layout();
        UpdateApplier.Swap(install, staged, backup);

        UpdateApplier.DiscardBackup(backup);

        Assert.IsFalse(Directory.Exists(backup));
        Assert.AreEqual("new", File.ReadAllText(Path.Combine(install, "marker.txt")));
    }

    [TestMethod]
    public void SwapThenRestore_RoundTripsExactly()
    {
        (string install, string staged, string backup) = Layout();
        File.WriteAllText(Path.Combine(install, "extra.txt"), "keep me");

        SwapOutcome outcome = UpdateApplier.Swap(install, staged, backup);

        Assert.IsFalse(outcome.Ok);
        Assert.AreEqual("old", File.ReadAllText(Path.Combine(install, "marker.txt")));
        Assert.AreEqual("keep me", File.ReadAllText(Path.Combine(install, "extra.txt")));
        Assert.IsFalse(Directory.Exists(backup));
    }

    [TestMethod]
    public void SwapExecutable_OnlyReplacesTheExecutableInADownloadsLikeDirectory()
    {
        string installDir = Path.Combine(_root, "Downloads");
        string stagedDir = Path.Combine(_root, "updates", "1.2.3", "staged");
        string backupDir = Path.Combine(_root, "updates", "backup");
        Directory.CreateDirectory(installDir);
        Directory.CreateDirectory(stagedDir);
        string installExe = Path.Combine(installDir, "MyProxy.exe");
        string stagedExe = Path.Combine(stagedDir, "MyProxy.exe");
        string backupExe = Path.Combine(backupDir, "MyProxy.exe");
        File.WriteAllText(installExe, "old");
        File.WriteAllText(stagedExe, "new");
        File.WriteAllText(Path.Combine(installDir, "family-photo.jpg"), "preserve");

        SwapOutcome outcome = UpdateApplier.SwapExecutable(installExe, stagedExe, backupExe);

        Assert.IsTrue(outcome.Ok, outcome.Detail);
        Assert.AreEqual("new", File.ReadAllText(installExe));
        Assert.AreEqual("new", File.ReadAllText(stagedExe));
        Assert.AreEqual("old", File.ReadAllText(backupExe));
        Assert.AreEqual("preserve", File.ReadAllText(Path.Combine(installDir, "family-photo.jpg")));

        SwapOutcome restored = UpdateApplier.RestoreExecutable(installExe, backupExe);
        Assert.IsTrue(restored.Ok, restored.Detail);
        Assert.AreEqual("old", File.ReadAllText(installExe));
        Assert.AreEqual("preserve", File.ReadAllText(Path.Combine(installDir, "family-photo.jpg")));
    }

    [TestMethod]
    public void SwapExecutable_RejectsPackagesWithSidecarFiles()
    {
        string installDir = Path.Combine(_root, "Downloads");
        string stagedDir = Path.Combine(_root, "staged");
        Directory.CreateDirectory(installDir);
        Directory.CreateDirectory(stagedDir);
        string installExe = Path.Combine(installDir, "MyProxy.exe");
        string stagedExe = Path.Combine(stagedDir, "MyProxy.exe");
        File.WriteAllText(installExe, "old");
        File.WriteAllText(stagedExe, "new");
        File.WriteAllText(Path.Combine(stagedDir, "sidecar.dll"), "must not be ignored");

        SwapOutcome outcome = UpdateApplier.SwapExecutable(installExe, stagedExe,
            Path.Combine(_root, "backup", "MyProxy.exe"));

        Assert.IsFalse(outcome.Ok);
        Assert.AreEqual("old", File.ReadAllText(installExe));
        Assert.AreEqual("new", File.ReadAllText(stagedExe));
    }

    [TestMethod]
    public void TryParseArgs_RequiresEveryArgument()
    {
        Assert.IsTrue(UpdateApplier.TryParseArgs(
            new[] { UpdateApplier.ApplySwitch, "a", "b", "c", "1234" },
            out string install, out string staged, out string backup, out int pid));
        Assert.AreEqual("a", install);
        Assert.AreEqual("b", staged);
        Assert.AreEqual("c", backup);
        Assert.AreEqual(1234, pid);

        // 参数不完整就什么都不做：宁可不动，也不要按猜出来的路径去移动目录。
        Assert.IsFalse(UpdateApplier.TryParseArgs(
            new[] { UpdateApplier.ApplySwitch, "a", "b" }, out _, out _, out _, out _));
        Assert.IsFalse(UpdateApplier.TryParseArgs(
            new[] { "--autostart" }, out _, out _, out _, out _));
        Assert.IsFalse(UpdateApplier.TryParseArgs(
            new[] { UpdateApplier.ApplySwitch, "a", "b", "c", "notapid" }, out _, out _, out _, out _));
    }

    [TestMethod]
    public void WaitForParentExit_ReturnsImmediatelyForAMissingProcess()
    {
        Assert.IsTrue(UpdateApplier.WaitForParentExit(0, TimeSpan.FromSeconds(1)));
        // 一个几乎不可能存在的 pid：进程已经没了正是我们要等的结果。
        Assert.IsTrue(UpdateApplier.WaitForParentExit(int.MaxValue - 1, TimeSpan.FromSeconds(1)));
    }
}

[TestClass]
public class FeatureFlagsTests
{
    private static FeatureFlags Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return FeatureFlags.FromJson(document.RootElement);
    }

    [TestMethod]
    public void ScalarsAreKept()
    {
        FeatureFlags flags = Parse("""{"a": true, "b": false, "c": 42, "d": "text", "e": null}""");

        Assert.IsTrue(flags.IsEnabled("a"));
        Assert.IsFalse(flags.IsEnabled("b"));
        Assert.AreEqual(42, flags.GetInt64("c"));
        Assert.AreEqual("text", flags.GetString("d"));
        Assert.AreEqual("", flags.GetString("e", "fallback"));
    }

    [TestMethod]
    public void NestedValuesAreDroppedNotAccepted()
    {
        // 嵌套正是把结构化指令夹带进一条只该传开关的通道的形状。
        FeatureFlags flags = Parse("""{"ok": true, "nested": {"x": 1}, "list": [1,2]}""");

        Assert.IsTrue(flags.IsEnabled("ok"));
        Assert.AreEqual(1, flags.Count);
    }

    [TestMethod]
    public void UnknownFlagsDoNotBreakTheKnownOnes()
    {
        // 服务端新加的开关不该让老客户端连已有的开关都读不到。
        FeatureFlags flags = Parse($$"""{"{{KnownFeatureFlags.UsageCategories}}": true, "somethingNew": "x"}""");

        Assert.IsTrue(flags.IsEnabled(KnownFeatureFlags.UsageCategories));
        Assert.AreEqual(2, flags.Count);
    }

    [TestMethod]
    public void NonObjectPayloadYieldsEmpty()
    {
        Assert.AreEqual(0, Parse("[]").Count);
        Assert.AreEqual(0, Parse("null").Count);
        Assert.AreEqual(0, FeatureFlags.Empty.Count);
    }

    [TestMethod]
    public void MissingFlagIsDisabled()
    {
        Assert.IsFalse(FeatureFlags.Empty.IsEnabled(KnownFeatureFlags.DisableAutoUpdate));
        Assert.AreEqual(7, FeatureFlags.Empty.GetInt64("missing", 7));
    }
}

[TestClass]
public class ReleaseSigningKeyTests
{
    [TestMethod]
    public void ParsesAValidSpec()
    {
        string hex = Convert.ToHexString(TestSigner.PublicKey(
            Enumerable.Range(1, 32).Select(i => (byte)i).ToArray())).ToLowerInvariant();

        IReadOnlyDictionary<string, byte[]> keys = ReleaseSigningKeys.Parse($"k1:{hex}");

        Assert.AreEqual(1, keys.Count);
        Assert.AreEqual(32, keys["k1"].Length);
    }

    [DataTestMethod]
    [DataRow("k1")]
    [DataRow("k1:")]
    [DataRow(":aabb")]
    [DataRow("k1:zz")]
    public void MalformedSpecsThrowInsteadOfSilentlyDroppingAKey(string spec)
    {
        // 一个拼写错误静默地把某把密钥从信任表里去掉，比整体失败糟得多。
        Assert.ThrowsException<ArgumentException>(() => ReleaseSigningKeys.Parse(spec));
    }

    [TestMethod]
    public void DuplicateKeyIdIsRejected()
    {
        string hex = new string('a', 64);
        Assert.ThrowsException<ArgumentException>(() => ReleaseSigningKeys.Parse($"k1:{hex},k1:{hex}"));
    }

    [TestMethod]
    public void EmptySpecYieldsNoKeys_WhichMeansRefuseEveryUpdate()
    {
        Assert.AreEqual(0, ReleaseSigningKeys.Parse("").Count);
        Assert.AreEqual(0, ReleaseSigningKeys.Parse(null).Count);
    }

    [TestMethod]
    public void ShippedKeySetIsEmptyUntilRealMaterialExists()
    {
        // 真公钥落表的那次提交里被一起改掉，顺便逼着改的人看见 fail-closed
        // 这件事已经不再成立。**绝不能为了让它通过而塞一个占位公钥。**
        Assert.AreEqual(0, ReleaseSigningKeys.Trusted.Count);
    }
}
