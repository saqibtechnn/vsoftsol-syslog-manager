namespace VSoftSol.Syslog.Core.Events;

/// <summary>
/// A single extracted field for an event (<c>event_fields(event_id, name, value)</c>).
/// Indexed, so vendor parser packs can add fields without a schema migration.
/// </summary>
/// <param name="Name">Field name, e.g. <c>src_ip</c>, <c>username</c>, <c>acl_name</c>.</param>
/// <param name="Value">Field value as extracted from the message, unmodified.</param>
public readonly record struct EventField(string Name, string Value);
