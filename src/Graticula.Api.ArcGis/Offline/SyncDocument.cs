using System;
using System.Linq;
using System.Text.Json.Nodes;

namespace Graticula.Api.ArcGis.Offline;

/// <summary>
/// What a sync-enabled feature service and its layers say about offline sync — ADR-288 §5.1.
/// </summary>
/// <remarks>
/// <para>
/// <b>The shape is the one ArcGIS Server 12.1 wrote for a hosted, sync-enabled service</b> (captured 2026-11-10,
/// outside the repository): <c>syncEnabled</c>, <c>syncCapabilities</c> and <c>Sync</c> in <c>capabilities</c>. Two of
/// the capabilities are false here because this server does not do them: <c>supportsPerReplicaSync</c> (Enterprise's
/// hosted services refuse <c>perReplica</c>, ADR-288 §4) and <c>supportsRollbackOnFailure</c>.
/// </para>
/// <para>
/// <b><c>supportsDisconnectedEditing</c> is true</b>, as ADR-288 §5.1 decides. The captured hosted service said false
/// for it at the service level; the ADR's reading is that a client deciding whether a layer may be taken offline is
/// told yes, and the capture is recorded here so the difference is visible if a client turns out to read it.
/// </para>
/// <para>
/// <b><c>Sync</c> is said to a caller who may edit</b> — one whose capabilities already carry an edit — because only
/// such a caller may create or synchronise a replica here (the task of ADR-288 §5.7(b)); telling a reader they may
/// would put a <i>Take offline</i> button in front of somebody who will be refused when they press it, which is what
/// the per-caller capabilities string exists to prevent.
/// </para>
/// </remarks>
public static class SyncDocument
{
    /// <summary>Whether a capabilities string offers an edit, and so whether its caller may sync.</summary>
    /// <param name="capabilities">The capabilities.</param>
    /// <returns>True when it carries Create, Update, Delete or Editing.</returns>
    public static bool OffersSync(string capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);

        return capabilities
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(op => op is "Create" or "Update" or "Delete" or "Editing");
    }

    /// <summary>The capabilities with <c>Sync</c> added where its caller may sync, and unchanged otherwise.</summary>
    /// <param name="capabilities">The capabilities.</param>
    /// <returns>The capabilities to write.</returns>
    public static string WithSync(string capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);

        if (!OffersSync(capabilities)
            || capabilities.Split(',', StringSplitOptions.TrimEntries).Contains("Sync", StringComparer.Ordinal))
        {
            return capabilities;
        }

        return capabilities.Length == 0 ? "Sync" : capabilities + ",Sync";
    }

    /// <summary>The <c>syncCapabilities</c> object, a new instance each time.</summary>
    /// <returns>The object.</returns>
    public static JsonObject SyncCapabilities() => new()
    {
        ["supportsAsync"] = true,
        ["supportsRegisteringExistingData"] = false,
        ["supportsSyncDirectionControl"] = true,
        ["supportsPerLayerSync"] = true,
        ["supportsPerReplicaSync"] = false,
        ["supportsSyncModelNone"] = true,
        ["supportsRollbackOnFailure"] = false,
        ["supportsAttachmentsSyncDirection"] = true,
        ["supportsBiDirectionalSyncForServer"] = false,
        ["supportsRegisterReplicaForServer"] = false,
        ["supportsExportDomainDescription"] = false,
        ["supportsDurableUrlForSyncModelNone"] = false,

        // ArcGIS's bit set of the data options a replica may carry; 57344 is what the captured service said.
        ["supportedSyncDataOptions"] = 57344,
    };

    /// <summary>Makes a service document say it is sync-enabled.</summary>
    /// <param name="document">The document, as JSON.</param>
    public static void ApplyToService(JsonObject document)
    {
        ArgumentNullException.ThrowIfNull(document);

        string capabilities = document["capabilities"]?.GetValue<string>() ?? string.Empty;
        document["capabilities"] = WithSync(capabilities);
        document["syncEnabled"] = true;
        document["syncCapabilities"] = SyncCapabilities();
        document["supportsDisconnectedEditing"] = true;
        document["supportsSyncModelNone"] = true;
    }

    /// <summary>Makes a layer document say its service is sync-enabled.</summary>
    /// <param name="document">The document, as JSON.</param>
    public static void ApplyToLayer(JsonObject document)
    {
        ArgumentNullException.ThrowIfNull(document);

        string capabilities = document["capabilities"]?.GetValue<string>() ?? string.Empty;
        document["capabilities"] = WithSync(capabilities);
        document["syncCanReturnChanges"] = true;
        document["supportsDisconnectedEditing"] = true;
    }
}
