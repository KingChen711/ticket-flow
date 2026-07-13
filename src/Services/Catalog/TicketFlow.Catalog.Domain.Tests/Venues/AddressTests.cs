using TicketFlow.Catalog.Domain.Venues;

namespace TicketFlow.Catalog.Domain.Tests.Venues;

public class AddressTests
{
    [Fact]
    public void Create_WithSameValues_AreEqual()
    {
        var a = Address.Create("123 Le Loi", "Ho Chi Minh City", "Vietnam", "700000");
        var b = Address.Create("123 Le Loi", "Ho Chi Minh City", "Vietnam", "700000");

        Assert.Equal(a, b);
        Assert.True(a == b);
    }

    [Fact]
    public void Create_WithDifferentValues_AreNotEqual()
    {
        var a = Address.Create("123 Le Loi", "Ho Chi Minh City", "Vietnam");
        var b = Address.Create("456 Nguyen Hue", "Ho Chi Minh City", "Vietnam");

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Create_WithEmptyStreet_Throws()
    {
        Assert.Throws<ArgumentException>(() => Address.Create("", "Ho Chi Minh City", "Vietnam"));
    }
}
