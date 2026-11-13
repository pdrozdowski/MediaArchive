using System.Text.Json;
using MediaArchive.Core;
Console.WriteLine(JsonSerializer.Serialize(ArchiveCatalogStore.Read(args[0])));
