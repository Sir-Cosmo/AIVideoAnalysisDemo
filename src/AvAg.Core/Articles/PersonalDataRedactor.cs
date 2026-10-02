using System.Text.RegularExpressions;

namespace AvAg.Core;

/// <summary>
/// Last line of defence before an article leaves the system: removes personal data that can be recognised by its
/// shape – e-mail addresses, phone numbers, IBANs, Swiss AHV numbers, card numbers, values after "Kundennummer" /
/// "customer number" and the like, names after "Herr/Frau/Mr/Ms" or "mein Name ist / my name is", and company names
/// ("Firma X", "X AG", "X GmbH").
/// It cannot find every name or company; the language model is told to leave them out, and a person should review
/// articles before publishing (the Markdown is marked <c>status: draft</c>).
/// </summary>
public static class PersonalDataRedactor
{
    private const RegexOptions Opts = RegexOptions.Compiled | RegexOptions.CultureInvariant;
    private const string Name = @"\p{Lu}[\p{Ll}'-]+(?:[- ]\p{Lu}[\p{Ll}'-]+)?";

    // Order matters: the specific number formats first, phone numbers last.
    private static readonly Regex[] Patterns =
    [
        new(@"\b[A-Z]{2}\d{2}(?:[ ]?[A-Z0-9]{4}){3,7}(?:[ ]?[A-Z0-9]{1,4})?\b", Opts),                       // IBAN
        new(@"\b756[.\s]?\d{4}[.\s]?\d{4}[.\s]?\d{2}\b", Opts),                                              // AHV number
        new(@"[\w.+-]+@[\w-]+(?:\.[\w-]+)+", Opts),                                                          // e-mail
        new(@"\b(?:\d[ -]?){13,19}\b", Opts),                                                                // card numbers
        new(@"(?:\+|\b00)\d{2}[\s/.-]?\(?\d{1,4}\)?(?:[\s/.-]?\d{2,4}){2,4}\b", Opts),                        // international phone
        new(@"\b0\d{2}[\s/.-]?\d{3}[\s/.-]?\d{2}[\s/.-]?\d{2}\b", Opts),                                     // Swiss phone 0xx xxx xx xx
        new(@"\b0\d{3,4}[\s/-]\d{3,8}\b", Opts),                                                             // other national phone
    ];

    // Label + value: the label stays, the value goes. After an explicit separator (":", "lautet", "=", "#") any word is
    // the value; otherwise (also after "ist"/"is") only something shaped like an identifier – with a digit, "_" or "@" –
    // so "Geben Sie Ihr Passwort ein." or "Das Passwort ist falsch." stay intact.
    private const string Labels =
        @"Kunden(?:-?nummer|-?nr\.?)|Vertrags(?:-?nummer|-?nr\.?)|Lizenz(?:-?nummer|-?schlüssel|-?key)|Seriennummer|Mandant(?:en-?nummer)?|Benutzername|Passwort|Kennwort|" +
        @"customer (?:number|no\.?|id)|account (?:number|id)|contract number|licen[cs]e (?:number|key)|serial number|user ?name|password";
    private const string Identifier = @"(?:[\w-]|[./@](?=\w))*[\d_@](?:[\w-]|[./@](?=\w))*";
    private static readonly Regex LabelledRx = new(
        $@"(?<label>\b(?:{Labels})\b\s*(?:lautet|:|=|#)\s*)(?<value>[^\s,;]*[^\s.,;])" +
        $@"|(?<label>\b(?:{Labels})\b\s*(?:(?:ist|is)\s+)?)(?<value>{Identifier})",
        Opts | RegexOptions.IgnoreCase);
    // Case-sensitive on purpose: a name starts with a capital letter, ordinary words after "this is" do not.
    private static readonly Regex TitledNameRx = new($@"\b(?<title>Herrn?|Frau|Hr\.|Fr\.|Mr\.?|Mrs\.?|Ms\.?)\s+{Name}", Opts);
    private static readonly Regex IntroducedNameRx = new(
        $@"(?<intro>\b(?:[Mm]ein Name ist|[Ii]ch hei(?:ss|ß)e|[Mm]y name is)\s+){Name}" +
        $@"|(?<intro>\b(?:[Hh]ier ist|[Hh]ier spricht|[Tt]his is)\s+){Name}(?=\s+(?:von|vom|bei|from|at)\b)", Opts);
    private static readonly Regex CompanyRx = new(
        $@"\b(?<label>Firma|Fa\.|[Cc]ompany)\s+{Name}(?:\s+(?:AG|GmbH|SA|Sàrl|KG|Ltd\.?|Inc\.?))?|\b{Name}(?:\s+{Name})?\s+(?:AG|GmbH|Sàrl|KG)\b", Opts);

    /// <summary>Returns the redacted text and the number of removed snippets.</summary>
    public static (string Text, int Count) Redact(string text, string language)
    {
        string token = language == "de" ? "[entfernt]" : "[removed]";
        int count = 0;
        string Count(string replacement) { count++; return replacement; }

        foreach (var rx in Patterns) text = rx.Replace(text, _ => Count(token));
        text = LabelledRx.Replace(text, m => Count(m.Groups["label"].Value + token));
        text = TitledNameRx.Replace(text, m => Count(m.Groups["title"].Value + " " + token));
        text = IntroducedNameRx.Replace(text, m => Count(m.Groups["intro"].Value + token));
        text = CompanyRx.Replace(text, m => Count((m.Groups["label"].Success ? m.Groups["label"].Value + " " : "") + token));
        return (text, count);
    }

    /// <summary>Redacts every text field of the article in place; returns the number of removed snippets.</summary>
    public static int Redact(WikiArticle a)
    {
        int total = 0;
        string? R(string? s)
        {
            if (s is null) return null;
            var (text, n) = Redact(s, a.Language);
            total += n;
            return text;
        }
        List<string> RL(List<string> l) => l.Select(x => R(x)!).ToList();

        a.Title = R(a.Title)!;
        a.Problem = R(a.Problem); a.Cause = R(a.Cause); a.AppliesTo = R(a.AppliesTo); a.Verification = R(a.Verification);
        a.ErrorMessages = RL(a.ErrorMessages); a.Notes = RL(a.Notes); a.Keywords = RL(a.Keywords);
        foreach (var s in a.Steps)
        {
            s.Section = R(s.Section); s.Title = R(s.Title); s.Instruction = R(s.Instruction)!; s.Details = R(s.Details);
        }
        a.Redactions += total;
        return total;
    }
}
