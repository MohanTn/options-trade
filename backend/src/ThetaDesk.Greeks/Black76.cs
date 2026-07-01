namespace ThetaDesk.Greeks;

/// <summary>
/// Black-76 pricing model for index options (futures as underlying).
/// All inputs: F = forward/futures price, K = strike, T = time to expiry (years),
/// r = risk-free rate, sigma = implied vol (annual). Returns per-unit Greeks.
/// </summary>
public static class Black76
{
    public static GreeksResult ComputeCall(double f, double k, double t, double r, double sigma)
    {
        if (t <= 0) return ZeroExpiry(f, k);
        return Compute(f, k, t, r, sigma, isCall: true);
    }

    public static GreeksResult ComputePut(double f, double k, double t, double r, double sigma)
    {
        if (t <= 0) return ZeroExpiry(f, k, isCall: false);
        return Compute(f, k, t, r, sigma, isCall: false);
    }

    public static double SolveIv(double marketPrice, double f, double k, double t, double r, bool isCall,
        double lo = 0.001, double hi = 5.0, int maxIter = 100)
    {
        if (t <= 0 || marketPrice <= 0) return 0;
        for (int i = 0; i < maxIter; i++)
        {
            double mid = (lo + hi) / 2;
            double price = isCall
                ? Compute(f, k, t, r, mid, isCall: true).Price
                : Compute(f, k, t, r, mid, isCall: false).Price;
            if (Math.Abs(price - marketPrice) < 0.01) return mid;
            if (price < marketPrice) lo = mid; else hi = mid;
        }
        return (lo + hi) / 2;
    }

    private static GreeksResult Compute(double f, double k, double t, double r, double sigma, bool isCall)
    {
        double sqrtT = Math.Sqrt(t);
        double d1 = (Math.Log(f / k) + 0.5 * sigma * sigma * t) / (sigma * sqrtT);
        double d2 = d1 - sigma * sqrtT;
        double df = Math.Exp(-r * t);

        double nd1 = isCall ? N(d1) : N(-d1) * -1 + N(d1); // reuse
        double nd2 = isCall ? N(d2) : N(-d2) * -1 + N(d2);

        // recalculate properly
        double price, delta;
        if (isCall)
        {
            price = df * (f * N(d1) - k * N(d2));
            delta = df * N(d1);
        }
        else
        {
            price = df * (k * N(-d2) - f * N(-d1));
            delta = -df * N(-d1);
        }

        double npd1 = Npdf(d1);
        double gamma = df * npd1 / (f * sigma * sqrtT);
        double theta = (-(f * sigma * npd1 * df) / (2 * sqrtT)
                        + (isCall ? -1 : 1) * r * k * df * (isCall ? N(d2) : N(-d2))) / 365.0;
        double vega = f * df * npd1 * sqrtT / 100.0; // per 1% vol move
        double rho = (isCall ? 1 : -1) * k * t * df * (isCall ? N(d2) : N(-d2)) / 100.0;

        return new GreeksResult(price, delta, gamma, theta, vega, rho, sigma);
    }

    private static GreeksResult ZeroExpiry(double f, double k, bool isCall = true)
    {
        double intrinsic = isCall ? Math.Max(f - k, 0) : Math.Max(k - f, 0);
        double delta = isCall ? (f > k ? 1 : 0) : (f < k ? -1 : 0);
        return new GreeksResult(intrinsic, delta, 0, 0, 0, 0, 0);
    }

    private static double N(double x) =>
        0.5 * (1 + Erf(x / Math.Sqrt(2)));

    private static double Npdf(double x) =>
        Math.Exp(-0.5 * x * x) / Math.Sqrt(2 * Math.PI);

    // Abramowitz & Stegun approximation, error < 1.5e-7
    private static double Erf(double x)
    {
        double t = 1.0 / (1.0 + 0.3275911 * Math.Abs(x));
        double poly = t * (0.254829592 + t * (-0.284496736 + t * (1.421413741 + t * (-1.453152027 + t * 1.061405429))));
        double result = 1 - poly * Math.Exp(-x * x);
        return x >= 0 ? result : -result;
    }
}

public record GreeksResult(double Price, double Delta, double Gamma, double Theta, double Vega, double Rho, double Iv);
