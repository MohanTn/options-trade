namespace ThetaDesk.Greeks;

public record PortfolioGreeks(
    double NetDelta,
    double NetGamma,
    double NetTheta,
    double NetVega,
    double NetRho);

public record LegGreeksInput(
    string TradingSymbol,
    bool IsCall,
    bool IsBuy,
    double Strike,
    double ForwardPrice,
    double TimeToExpiryYears,
    double RiskFreeRate,
    double ImpliedVol,
    int Qty);

public static class GreeksAggregator
{
    public static (GreeksResult Greeks, double Iv) ComputeLeg(LegGreeksInput leg)
    {
        double sigma = leg.ImpliedVol > 0 ? leg.ImpliedVol : 0.15;
        var g = leg.IsCall
            ? Black76.ComputeCall(leg.ForwardPrice, leg.Strike, leg.TimeToExpiryYears, leg.RiskFreeRate, sigma)
            : Black76.ComputePut(leg.ForwardPrice, leg.Strike, leg.TimeToExpiryYears, leg.RiskFreeRate, sigma);

        // Flip sign for short legs
        double sign = leg.IsBuy ? 1 : -1;
        var adjusted = new GreeksResult(
            g.Price,
            g.Delta * sign * leg.Qty,
            g.Gamma * sign * leg.Qty,
            g.Theta * sign * leg.Qty,
            g.Vega * sign * leg.Qty,
            g.Rho * sign * leg.Qty,
            g.Iv);

        return (adjusted, sigma);
    }

    public static PortfolioGreeks Aggregate(IEnumerable<GreeksResult> legGreeks)
    {
        double delta = 0, gamma = 0, theta = 0, vega = 0, rho = 0;
        foreach (var g in legGreeks)
        {
            delta += g.Delta;
            gamma += g.Gamma;
            theta += g.Theta;
            vega += g.Vega;
            rho += g.Rho;
        }
        return new PortfolioGreeks(delta, gamma, theta, vega, rho);
    }

    public static double SolveIv(double marketPrice, LegGreeksInput leg) =>
        Black76.SolveIv(marketPrice, leg.ForwardPrice, leg.Strike,
            leg.TimeToExpiryYears, leg.RiskFreeRate, leg.IsCall);
}
