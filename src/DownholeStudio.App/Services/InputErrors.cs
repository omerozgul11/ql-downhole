namespace DownholeStudio.App.Services;

/// <summary>Turns input conversion errors into a message for the user instead of a programmer's exception text.</summary>
public static class InputErrors
{
    public const string NotANumber = "Enter a number, for example 2.5.";

    public static object? Friendly(object? error) => error switch
    {
        InvalidCastException or FormatException => NotANumber,
        Exception ex when ex.Message.Contains("Could not convert", StringComparison.Ordinal) => NotANumber,
        Exception ex => ex.Message,
        _ => error,
    };
}
