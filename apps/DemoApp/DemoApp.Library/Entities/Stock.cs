namespace DemoApp.Library.Entities;

public class Stock
{
    /// <summary>Unique identifier of this stock, derived from its ticker symbol.</summary>
    public string? Id { get; set; }
    /// <summary>Ticker symbol under which the stock trades, for example <c>MSFT</c>.</summary>
    public string Symbol { get; set; } = string.Empty;
    /// <summary>Name of the company whose shares this stock represents.</summary>
    public string CompanyName { get; set; } = string.Empty;
    /// <summary>Latest known price of one share.</summary>
    public decimal CurrentPrice { get; set; }
    /// <summary>Difference between the current price and the base price, in currency units; negative when the price dropped.</summary>
    public decimal Change { get; set; }
    /// <summary>Price change relative to the base price, expressed as a percentage.</summary>
    public decimal ChangePercent { get; set; }
}
