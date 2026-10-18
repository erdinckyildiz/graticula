using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Identity;
using Graticula.Platform.Secrets;
using Xunit;

namespace Graticula.Platform.Tests.Identity;

/// <summary>A second step at sign-in — ADR-201: RFC 6238's codes, spent once, and recovery codes spent once.</summary>
public sealed class SecondFactorTests
{
    /// <summary>RFC 6238 appendix B, SHA-1, T = 59 s: 94287082, whose last six digits are what a six-digit app shows.</summary>
    [Fact]
    public void The_rfc_test_vector()
    {
        byte[] secret = Encoding.ASCII.GetBytes("12345678901234567890");

        Assert.Equal("287082", Totp.CodeAt(secret, Totp.StepOf(DateTimeOffset.FromUnixTimeSeconds(59))));
    }

    [Fact]
    public void Base32_is_rfc_4648s()
    {
        Assert.Equal("MZXW6YTBOI", Totp.Base32(Encoding.ASCII.GetBytes("foobar")));
    }

    [Fact]
    public async Task Enrolling_needs_a_code_and_a_code_is_spent_once()
    {
        Clock clock = new(DateTimeOffset.FromUnixTimeSeconds(1_800_000_000));
        Memory store = new();
        SecondFactors factors = new(store, new SecretProtector(1, SecretProtector.GenerateKey()), clock);
        Principal member = new(Guid.NewGuid(), PrincipalKind.User, "zz_member", null, isDisabled: false);

        (string secret, string address) = (await factors.BeginAsync(member, "Graticula", CancellationToken.None))!.Value;
        Assert.StartsWith("otpauth://totp/", address, StringComparison.Ordinal);
        Assert.False(await factors.RequiredAsync(member.Id, CancellationToken.None));

        byte[] raw = FromBase32(secret);

        IReadOnlyList<string>? recovery = await factors.ConfirmAsync(
            member.Id, Totp.CodeAt(raw, Totp.StepOf(clock.Now)), CancellationToken.None);
        Assert.NotNull(recovery);
        Assert.Equal(SecondFactors.RecoveryCodes, recovery!.Count);
        Assert.True(await factors.RequiredAsync(member.Id, CancellationToken.None));

        // The code that confirmed is spent; the next step's passes once.
        Assert.False(await factors.VerifyAsync(member.Id, Totp.CodeAt(raw, Totp.StepOf(clock.Now)), CancellationToken.None));
        clock.Now = clock.Now.AddSeconds(Totp.Period);
        string next = Totp.CodeAt(raw, Totp.StepOf(clock.Now));
        Assert.True(await factors.VerifyAsync(member.Id, next, CancellationToken.None));
        Assert.False(await factors.VerifyAsync(member.Id, next, CancellationToken.None));

        // A recovery code passes once.
        Assert.True(await factors.VerifyAsync(member.Id, recovery[0], CancellationToken.None));
        Assert.False(await factors.VerifyAsync(member.Id, recovery[0], CancellationToken.None));

        // Begun again while on: refused.
        Assert.Null(await factors.BeginAsync(member, "Graticula", CancellationToken.None));
    }

    [Fact]
    public async Task A_wrong_code_does_not_confirm()
    {
        Clock clock = new(DateTimeOffset.FromUnixTimeSeconds(1_800_000_000));
        Memory store = new();
        SecondFactors factors = new(store, new SecretProtector(1, SecretProtector.GenerateKey()), clock);
        Principal member = new(Guid.NewGuid(), PrincipalKind.User, "zz_member", null, isDisabled: false);
        (string secret, _) = (await factors.BeginAsync(member, "Graticula", CancellationToken.None))!.Value;
        string right = Totp.CodeAt(FromBase32(secret), Totp.StepOf(clock.Now));
        string wrong = right == "123456" ? "654321" : "123456";

        Assert.Null(await factors.ConfirmAsync(member.Id, wrong, CancellationToken.None));
        Assert.False(await factors.RequiredAsync(member.Id, CancellationToken.None));
    }

    private static byte[] FromBase32(string text)
    {
        const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        List<byte> bytes = [];
        int buffer = 0, bits = 0;

        foreach (char c in text)
        {
            buffer = (buffer << 5) | Alphabet.IndexOf(c, StringComparison.Ordinal);
            bits += 5;

            if (bits >= 8)
            {
                bytes.Add((byte)((buffer >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }

        return [.. bytes];
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Memory : ISecondFactorStore
    {
        private readonly Dictionary<Guid, SecondFactorRecord> _rows = [];

        public Task<SecondFactorRecord?> FindAsync(Guid principal, CancellationToken cancellationToken) =>
            Task.FromResult(_rows.TryGetValue(principal, out SecondFactorRecord? row) ? row : null);

        public Task<bool> BeginAsync(Guid principal, byte[] sealedSecret, int keyVersion, CancellationToken cancellationToken)
        {
            if (_rows.TryGetValue(principal, out SecondFactorRecord? row) && row.Enabled)
            {
                return Task.FromResult(false);
            }

            _rows[principal] = new SecondFactorRecord(sealedSecret, keyVersion, false, 0, []);
            return Task.FromResult(true);
        }

        public Task<bool> EnableAsync(Guid principal, long timeStep, IReadOnlyList<byte[]> recovery, CancellationToken cancellationToken)
        {
            SecondFactorRecord row = _rows[principal];
            _rows[principal] = row with { Enabled = true, LastStep = timeStep, Recovery = recovery };
            return Task.FromResult(true);
        }

        public Task<bool> AdvanceAsync(Guid principal, long timeStep, CancellationToken cancellationToken)
        {
            SecondFactorRecord row = _rows[principal];

            if (row.LastStep >= timeStep)
            {
                return Task.FromResult(false);
            }

            _rows[principal] = row with { LastStep = timeStep };
            return Task.FromResult(true);
        }

        public Task<bool> SpendRecoveryAsync(Guid principal, byte[] hash, CancellationToken cancellationToken)
        {
            SecondFactorRecord row = _rows[principal];
            byte[]? found = row.Recovery.FirstOrDefault(h => h.SequenceEqual(hash));

            if (found is null)
            {
                return Task.FromResult(false);
            }

            _rows[principal] = row with { Recovery = [.. row.Recovery.Where(h => !ReferenceEquals(h, found))] };
            return Task.FromResult(true);
        }

        public Task<bool> RemoveAsync(Guid principal, CancellationToken cancellationToken) =>
            Task.FromResult(_rows.Remove(principal));
    }
}
