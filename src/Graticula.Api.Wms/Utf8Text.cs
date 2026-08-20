using System.IO;
using System.Text;

namespace Graticula.Api.Wms;

/// <summary>
/// A <see cref="StringWriter"/> that admits it will be sent as UTF-8.
/// </summary>
/// <remarks>
/// <para>
/// <b>Because <c>XmlWriter</c> over a <c>StringBuilder</c> declares
/// <c>encoding="utf-16"</c>, whatever its settings say.</b> It asks the writer what
/// encoding it uses, and a .NET string is UTF-16, so the declaration is honest about
/// the buffer and wrong about the wire — the response goes out as UTF-8 and carries a
/// header saying otherwise. <c>XmlWriterSettings.Encoding</c> does not change it;
/// only the writer's own <c>Encoding</c> property does.
/// </para>
/// <para>
/// <b>Caught 2026-08-20, in the first WMS capabilities document served.</b> Most
/// parsers ignore the declaration when the bytes obviously are not UTF-16, which is
/// exactly why this survives review: it works everywhere until it meets a strict
/// parser or a non-ASCII layer name, and then it fails as *malformed document*.
/// </para>
/// </remarks>
internal sealed class Utf8Text : StringWriter
{
    /// <inheritdoc/>
    public override Encoding Encoding => Encoding.UTF8;
}
