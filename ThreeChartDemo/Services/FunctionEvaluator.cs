using System.Globalization;
using NCalc;
using NCalc.Exceptions;

namespace ThreeChartDemo.Services;

/// <summary>
/// Wraps an NCalc expression f(x, y, t). Supported: + - * / %, parentheses, comparison,
/// and functions such as Sin, Cos, Tan, Asin, Acos, Atan, Atan2, Sqrt, Abs, Exp, Log, Log10,
/// Pow, Floor, Ceiling, Round, Min, Max, Sign, IF(cond, a, b). Built-in function names are
/// case-insensitive (sin == Sin). Variables: x, y, t. Constants: pi, e.
/// NOTE: use Pow(x, 2) for powers ("^" is not power in NCalc).
/// </summary>
public sealed class FunctionEvaluator
{
    private readonly Expression _expression;

    public FunctionEvaluator(string formula)
    {
        _expression = new Expression(formula, ExpressionOptions.IgnoreCaseAtBuiltInFunctions);
        _expression.Parameters["pi"] = Math.PI;
        _expression.Parameters["e"] = Math.E;
        _expression.Parameters["x"] = 0.5;
        _expression.Parameters["y"] = 0.25;
        _expression.Parameters["t"] = 0.0;

        // Validate once: syntax errors / unknown functions / unknown variables throw NCalcException.
        try
        {
            _expression.Evaluate();
        }
        catch (NCalcException ex)
        {
            throw new FormatException(ex.Message, ex);
        }
        catch (Exception)
        {
            // e.g. a math error at the probe point - not a syntax problem, ignore.
        }
    }

    /// <summary>Evaluates f(x, y, t). Returns NaN where the function is undefined.</summary>
    public double Evaluate(double x, double y, double t)
    {
        _expression.Parameters["x"] = x;
        _expression.Parameters["y"] = y;
        _expression.Parameters["t"] = t;
        try
        {
            var result = _expression.Evaluate();
            return result is null ? double.NaN : Convert.ToDouble(result, CultureInfo.InvariantCulture);
        }
        catch (Exception)
        {
            return double.NaN;
        }
    }
}
