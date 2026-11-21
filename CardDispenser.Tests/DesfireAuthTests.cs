using CardDispenser.Core.Models;
using Xunit;

namespace CardDispenser.Tests;

public class DesfireAuthTests
{
    private static readonly byte[] CardKey = "test-desfire-key"u8.ToArray(); // 16 bytes (AES-128)
    private static readonly byte[] WrongKey = "test-wrong-key-1"u8.ToArray(); // 16 bytes, different content

    private static DesfireCard NewCard()
    {
        var card = new DesfireCard("04C1AAAA");
        card.ProvisionMasterKey(DesfireKeyType.Aes128, CardKey);
        card.SelectApplication(0x00A1A1);
        return card;
    }

    [Fact]
    public void ThreePass_CorrectKey_BothSidesDeriveTheSameSessionKey()
    {
        var card = NewCard();

        var (ok, readerKey) = DesfireAuthenticator.Authenticate(card, keyNo: 0, CardKey);

        Assert.True(ok);
        Assert.NotNull(readerKey);
        Assert.NotNull(card.SessionKey);
        Assert.Equal(16, readerKey!.Length);
        Assert.Equal(card.SessionKey, readerKey);
    }

    [Fact]
    public void ThreePass_WrongKey_FailsWithoutSessionKeyAndFileAccessIsDenied()
    {
        var card = NewCard();

        var (ok, readerKey) = DesfireAuthenticator.Authenticate(card, keyNo: 0, WrongKey);

        Assert.False(ok);
        Assert.Null(readerKey);
        Assert.Null(card.SessionKey);
        Assert.False(card.WriteFile(1, [1, 2, 3]));
        Assert.Null(card.ReadFile(1));
    }
}
