using TicketFlow.Catalog.Domain.Events;

namespace TicketFlow.Catalog.Domain.Tests.Events;

public class MoneyTests
{
    [Fact]
    public void Create_WithNegativeAmount_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Money.Create(-1));
    }

    [Fact]
    public void Create_WithZeroAmount_DoesNotThrow()
    {
        var money = Money.Create(0);

        Assert.Equal(0, money.Amount);
    }

    [Fact]
    public void Create_DefaultsToVndCurrency()
    {
        var money = Money.Create(100_000);

        Assert.Equal("VND", money.Currency);
    }

    [Fact]
    public void SameAmountAndCurrency_AreEqual()
    {
        Assert.Equal(Money.Create(500_000, "VND"), Money.Create(500_000, "VND"));
    }
}
