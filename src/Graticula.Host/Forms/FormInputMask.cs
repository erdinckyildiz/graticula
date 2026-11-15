using System;
using System.Collections.Generic;
using System.Linq;

namespace Graticula.Host.Forms;

/// <summary>
/// A <c>body::esri:inputMask</c>, read — ADR-287 §5.19 (5).
/// </summary>
/// <remarks>
/// <para>
/// <b>The syntax is the one Survey123's documentation gives</b> (the page on its custom XLSForm columns): mask characters
/// <c>A a N n X x 9 0 D d # H h B b</c> (capital required, lower case optional), the case switches <c>&gt;</c>, <c>&lt;</c>
/// and <c>!</c>, <c>\</c> to make a special character a separator, any other character a separator, and an optional
/// <c>;</c> and one character at the end that stands for a blank. The same grammar is Qt's <c>QLineEdit</c> input mask,
/// which Survey123's field app is built on; it is described by Qt's public documentation as well.
/// </para>
/// <para>
/// <b>The browser client has its own copy of this reading</b> (<c>fill.js</c>); the two are held to the same cases by
/// <c>AFormImportsSurvey123SpellingsTests</c> here and <c>FormFillCompatTests</c> there. The server keeps the column and
/// checks that the mask is one it can honour and that the longest answer it allows fits the field it is stored in; it
/// does not re-apply the mask to a submitted answer, for the same reason it does not re-evaluate <c>constraint</c>
/// (§5.9 (i)): the mask is a usability contract, and the schema is the integrity contract.
/// </para>
/// </remarks>
public sealed record FormInputMask(IReadOnlyList<InputMaskPart> Parts, char Placeholder, int Slots, int RequiredSlots, int MaxLength)
{
    /// <summary>The longest a mask may be, in the text of the cell.</summary>
    public const int MaxMaskText = 255;

    private const string SlotCharacters = "AaNnXx90DdHhBb#";

    /// <summary>Reads a mask: its parts when it is one this server honours, otherwise why not.</summary>
    /// <param name="text">The cell's text.</param>
    /// <param name="mask">The mask, when it can be read.</param>
    /// <param name="problem">Why it cannot be, otherwise null.</param>
    public static bool TryParse(string text, out FormInputMask? mask, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(text);

        mask = null;
        problem = null;

        if (text.Length == 0 || text.Length > MaxMaskText)
        {
            problem = text.Length == 0 ? "The mask is empty." : $"A mask is at most {MaxMaskText} characters.";
            return false;
        }

        // The blank is what follows the last ';' that is not escaped, when exactly one character follows it (or none,
        // which is a space). Anything else makes the ';' a separator.
        int cut = -1;

        for (int i = text.Length - 1; i >= 0; i--)
        {
            if (text[i] == ';' && !Escaped(text, i))
            {
                cut = i;
                break;
            }
        }

        char placeholder = ' ';
        string body = text;

        if (cut >= 0 && cut >= text.Length - 2)
        {
            body = text[..cut];
            placeholder = cut == text.Length - 2 ? text[^1] : ' ';
        }

        List<InputMaskPart> parts = [];
        char letterCase = '!';

        for (int i = 0; i < body.Length; i++)
        {
            char c = body[i];

            if (c == '\\')
            {
                if (i + 1 >= body.Length)
                {
                    problem = "The mask ends with a backslash, which has nothing to make a separator of.";
                    return false;
                }

                parts.Add(new InputMaskPart(body[++i], null, false, '!'));
            }
            else if (c is '>' or '<' or '!')
            {
                letterCase = c;
            }
            else if (SlotCharacters.Contains(c, StringComparison.Ordinal))
            {
                parts.Add(new InputMaskPart(c, char.ToUpperInvariant(c), c is 'A' or 'N' or 'X' or '9' or 'D' or 'H' or 'B', letterCase));
            }
            else
            {
                parts.Add(new InputMaskPart(c, null, false, '!'));
            }
        }

        int slots = parts.Count(p => p.Kind is not null);

        if (slots == 0)
        {
            problem = "The mask has no mask character in it (A, a, N, n, X, x, 9, 0, D, d, #, H, h, B or b), so it would accept nothing.";
            return false;
        }

        // A slot is filled in order, so the answer is whole when it reaches the last required slot.
        int lastRequired = 0;
        int seen = 0;

        foreach (InputMaskPart part in parts.Where(p => p.Kind is not null))
        {
            seen++;
            lastRequired = part.Required ? seen : lastRequired;
        }

        mask = new FormInputMask(parts, placeholder, slots, lastRequired, parts.Count);
        return true;
    }

    private static bool Escaped(string text, int index)
    {
        int slashes = 0;

        for (int i = index - 1; i >= 0 && text[i] == '\\'; i--)
        {
            slashes++;
        }

        return slashes % 2 == 1;
    }
}

/// <summary>One element of a mask: a separator, or a slot that takes one character.</summary>
/// <param name="Literal">The separator, or the mask character that makes the slot.</param>
/// <param name="Kind">The slot's mask character in upper case, or null for a separator.</param>
/// <param name="Required">Whether the slot must be filled.</param>
/// <param name="Case">The case switch in force: <c>&gt;</c> upper, <c>&lt;</c> lower, <c>!</c> neither.</param>
public sealed record InputMaskPart(char Literal, char? Kind, bool Required, char Case);
