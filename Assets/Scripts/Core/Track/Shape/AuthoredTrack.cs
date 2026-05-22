using System;
using System.Collections.Generic;
using System.IO;
using Unidad.Core.Grid;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.Track.Shape
{
    // -------------------------------------------------------------------------
    // Authored-track persistence layer.
    //
    // Authored tracks come from the in-game TrackPartsEditorScenario as a list of
    // (shape, gridPos, facing, variantIndex) tuples. This file holds:
    //   * AuthoredTrackData — JSON-friendly DTO + per-piece sub-DTO.
    //   * AuthoredTrackAsset — ScriptableObject wrapping the DTO so designers
    //     can drop authored tracks under Resources/AuthoredTracks/ for auto-load.
    //   * AuthoredTrackAssetLoader — Resources scan, mirrors TrackShapeAssetLoader.
    //   * AuthoredTrackJsonStore — disk read/write under persistentDataPath.
    //   * IAuthoredTrackCatalog / AuthoredTrackCatalog — registry the card source
    //     toggle reads from.
    //   * IShapeSourceSelector / ShapeSourceSelector — runtime toggle for which
    //     pool of cards the placement scenario serves up (BuiltIn / AuthoredOnly /
    //     Both). Card UI binds to this; built-in catalog enumeration is unchanged.
    //
    // Co-located in one file to minimise csproj-regen friction (per project memory:
    // "new .cs files break dotnet build until Unity refreshes").
    // -------------------------------------------------------------------------

    /// <summary>
    /// JSON-friendly snapshot of one piece in an authored track. Strings + ints only
    /// so <see cref="JsonUtility"/> handles it without custom converters. The
    /// <see cref="ShapeId"/> string round-trips through <see cref="TrackPieceShape"/>'s
    /// constructor.
    /// </summary>
    [Serializable]
    public struct AuthoredTrackPiece
    {
        public string ShapeId;
        public int X;
        public int Y;
        public int Facing;
        public int VariantIndex;
    }

    /// <summary>
    /// Top-level DTO serialized to disk and embedded inside <see cref="AuthoredTrackAsset"/>.
    /// </summary>
    [Serializable]
    public class AuthoredTrackData
    {
        public string Id;
        public string DisplayName;
        public AuthoredTrackPiece[] Pieces = Array.Empty<AuthoredTrackPiece>();
    }

    /// <summary>
    /// ScriptableObject wrapper for an authored track. Drop assets under
    /// <c>Resources/AuthoredTracks/</c> for <see cref="AuthoredTrackAssetLoader"/>
    /// to pick them up at runtime — same auto-discovery pattern as
    /// <see cref="TrackShapeAsset"/>.
    /// </summary>
    [CreateAssetMenu(menuName = "RACING/Authored Track", fileName = "AuthoredTrack", order = 110)]
    public sealed class AuthoredTrackAsset : ScriptableObject
    {
        [SerializeField] private AuthoredTrackData data = new();

        public AuthoredTrackData Data => data;
        public string Id => string.IsNullOrEmpty(data.Id) ? name : data.Id;
        public string DisplayName => string.IsNullOrEmpty(data.DisplayName) ? name : data.DisplayName;

        public IReadOnlyList<AuthoredTrackPiece> Pieces => data.Pieces ?? Array.Empty<AuthoredTrackPiece>();

        /// <summary>Replaces the embedded data wholesale. Used by the editor's "Save to Asset" path.</summary>
        public void SetData(AuthoredTrackData fresh)
        {
            data = fresh ?? new AuthoredTrackData();
        }
    }

    /// <summary>
    /// Auto-discovers <see cref="AuthoredTrackAsset"/>s under any
    /// <c>Resources/AuthoredTracks/</c> folder and registers them into the
    /// authored-track catalog. Mirrors <see cref="TrackShapeAssetLoader.LoadInto"/>
    /// — drop a new ScriptableObject in, restart play, the new track shows up.
    /// Already-registered ids are skipped (assets win over runtime JSON imports).
    /// </summary>
    public static class AuthoredTrackAssetLoader
    {
        public const string ResourcesPath = "AuthoredTracks";

        public static int LoadInto(IAuthoredTrackCatalog catalog)
        {
            if (catalog == null) return 0;
            var assets = Resources.LoadAll<AuthoredTrackAsset>(ResourcesPath);
            int registered = 0;
            for (int i = 0; i < assets.Length; i++)
            {
                var a = assets[i];
                if (a == null) continue;
                if (catalog.Has(a.Id)) continue;
                catalog.Register(a.Id, a.DisplayName, a.Pieces);
                registered++;
            }
            return registered;
        }
    }

    /// <summary>
    /// Disk-backed save/load under <c>Application.persistentDataPath/AuthoredTracks/</c>.
    /// Used by the in-game editor's F2/F3 keys. Asset-based authoring (the
    /// <see cref="AuthoredTrackAssetLoader"/> Resources path) is preferred for shipped
    /// content; this store is for runtime authoring + import/export.
    /// </summary>
    public static class AuthoredTrackJsonStore
    {
        /// <summary>
        /// Override the on-disk root used for the <c>AuthoredTracks/</c> sub-folder.
        /// Production: <c>null</c> (defaults to <see cref="Application.persistentDataPath"/>).
        /// Tests: assign a temp directory in [SetUp], restore to <c>null</c> in [TearDown]
        /// so saved fixtures don't pollute the user's persistent data.
        /// </summary>
        public static Func<string> RootProvider;

        private static string Folder
        {
            get
            {
                var root = RootProvider?.Invoke();
                if (string.IsNullOrEmpty(root)) root = Application.persistentDataPath;
                var dir = Path.Combine(root, "AuthoredTracks");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        public static string PathFor(string id) => Path.Combine(Folder, $"{Sanitise(id)}.json");

        public static void Save(string id, string displayName, IReadOnlyList<AuthoredTrackPiece> pieces)
        {
            var dto = new AuthoredTrackData
            {
                Id = id,
                DisplayName = string.IsNullOrEmpty(displayName) ? id : displayName,
                Pieces = ToArray(pieces),
            };
            File.WriteAllText(PathFor(id), JsonUtility.ToJson(dto, prettyPrint: true));
        }

        public static AuthoredTrackData Load(string id)
        {
            var path = PathFor(id);
            if (!File.Exists(path)) return null;
            return JsonUtility.FromJson<AuthoredTrackData>(File.ReadAllText(path));
        }

        public static IReadOnlyList<string> ListIds()
        {
            if (!Directory.Exists(Folder)) return Array.Empty<string>();
            var files = Directory.GetFiles(Folder, "*.json");
            var ids = new string[files.Length];
            for (int i = 0; i < files.Length; i++)
                ids[i] = Path.GetFileNameWithoutExtension(files[i]);
            return ids;
        }

        public static int LoadAllInto(IAuthoredTrackCatalog catalog)
        {
            if (catalog == null) return 0;
            var ids = ListIds();
            int registered = 0;
            for (int i = 0; i < ids.Count; i++)
            {
                var data = Load(ids[i]);
                if (data == null) continue;
                if (catalog.Has(data.Id)) continue;
                catalog.Register(data.Id, data.DisplayName, data.Pieces);
                registered++;
            }
            return registered;
        }

        private static AuthoredTrackPiece[] ToArray(IReadOnlyList<AuthoredTrackPiece> pieces)
        {
            if (pieces == null) return Array.Empty<AuthoredTrackPiece>();
            var arr = new AuthoredTrackPiece[pieces.Count];
            for (int i = 0; i < pieces.Count; i++) arr[i] = pieces[i];
            return arr;
        }

        private static string Sanitise(string s)
        {
            if (string.IsNullOrEmpty(s)) return "untitled";
            var invalid = Path.GetInvalidFileNameChars();
            var chars = s.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
                if (Array.IndexOf(invalid, chars[i]) >= 0) chars[i] = '_';
            return new string(chars);
        }
    }

    /// <summary>
    /// Registry of authored tracks available as cards. Card UI enumerates this
    /// alongside <see cref="TrackShapeCatalog"/>; <see cref="IShapeSourceSelector"/>
    /// decides which list(s) are visible in the deck.
    /// </summary>
    public interface IAuthoredTrackCatalog
    {
        bool Has(string id);
        void Register(string id, string displayName, IReadOnlyList<AuthoredTrackPiece> pieces);
        bool TryGet(string id, out AuthoredTrackData data);
        IReadOnlyCollection<AuthoredTrackData> All { get; }
        int Count { get; }
    }

    /// <summary>Default in-memory <see cref="IAuthoredTrackCatalog"/>.</summary>
    public sealed class AuthoredTrackCatalog : IAuthoredTrackCatalog
    {
        private readonly Dictionary<string, AuthoredTrackData> _byId = new();

        public int Count => _byId.Count;
        public IReadOnlyCollection<AuthoredTrackData> All => _byId.Values;

        public bool Has(string id) => !string.IsNullOrEmpty(id) && _byId.ContainsKey(id);

        public void Register(string id, string displayName, IReadOnlyList<AuthoredTrackPiece> pieces)
        {
            if (string.IsNullOrEmpty(id)) return;
            var arr = new AuthoredTrackPiece[pieces?.Count ?? 0];
            if (pieces != null) for (int i = 0; i < pieces.Count; i++) arr[i] = pieces[i];
            _byId[id] = new AuthoredTrackData
            {
                Id = id,
                DisplayName = string.IsNullOrEmpty(displayName) ? id : displayName,
                Pieces = arr,
            };
        }

        public bool TryGet(string id, out AuthoredTrackData data) => _byId.TryGetValue(id, out data);
    }

    /// <summary>Which shape source feeds the card UI deck.</summary>
    public enum ShapeSource : byte
    {
        BuiltIn = 0,
        AuthoredOnly = 1,
        Both = 2,
    }

    /// <summary>
    /// Runtime toggle for shape source. Card UI / scenario reads
    /// <see cref="Active"/> each time it rebuilds the deck. Default is
    /// <see cref="ShapeSource.Both"/> so authored tracks appear alongside
    /// built-ins out of the box.
    /// </summary>
    public interface IShapeSourceSelector
    {
        ShapeSource Active { get; set; }
    }

    /// <summary>Default <see cref="IShapeSourceSelector"/> backed by a single field.</summary>
    public sealed class ShapeSourceSelector : IShapeSourceSelector
    {
        public ShapeSource Active { get; set; } = ShapeSource.Both;
    }

    /// <summary>
    /// Raised by <see cref="AuthoredTrackToShapeConverter.Convert"/> when an authored
    /// track cannot be promoted to a runtime <see cref="TrackShape"/>. The loader
    /// catches this, logs a warning, and skips the offending file — the catalog stays
    /// healthy.
    /// </summary>
    public sealed class AuthoredTrackConversionException : Exception
    {
        public AuthoredTrackConversionException(string message) : base(message) { }
    }

    /// <summary>
    /// Converts a saved <see cref="AuthoredTrackData"/> JSON record into a runtime
    /// <see cref="TrackShape"/> usable by <see cref="IShapePlacementService.TryPlaceShape"/>.
    /// <para>
    /// Per-piece variants survive the round-trip via
    /// <see cref="TrackShapePiece.VariantOverride"/> — kerbs/walls authored on a card
    /// commit with their original variant regardless of any global variant passed at
    /// placement time.
    /// </para>
    /// <para>
    /// <b>Magnet compatibility invariant.</b> The min-origin shift applied here is
    /// pure translation. Port outward directions and the cardinal-mid-edge / diagonal-corner
    /// port lattice are invariant under translation, so converted shapes magnet-snap
    /// against seeded shapes and other converted shapes through the same
    /// <see cref="ShapeBoundaryPorts.Enumerate"/> path used by the rest of the catalog.
    /// No new boundary-port logic exists for authored cards.
    /// </para>
    /// </summary>
    public static class AuthoredTrackToShapeConverter
    {
        /// <summary>Prefix on the synthetic <see cref="TrackShapeId"/>. Avoids id
        /// collisions when authored cards coexist with seeded shapes (e.g. in tests).</summary>
        public const string IdPrefix = "authored:";

        /// <summary>
        /// Build a <see cref="TrackShape"/> from <paramref name="data"/>. Throws
        /// <see cref="AuthoredTrackConversionException"/> on null / empty / unknown-piece /
        /// self-overlapping inputs.
        /// </summary>
        public static TrackShape Convert(AuthoredTrackData data, ITrackPieceCatalog pieceCatalog)
        {
            if (data == null) throw new AuthoredTrackConversionException("AuthoredTrackData is null");
            if (pieceCatalog == null) throw new AuthoredTrackConversionException("ITrackPieceCatalog is null");
            var src = data.Pieces ?? Array.Empty<AuthoredTrackPiece>();
            if (src.Length == 0)
                throw new AuthoredTrackConversionException(
                    $"authored track '{data.Id}' has no pieces");

            int minX = int.MaxValue, minY = int.MaxValue;
            for (int i = 0; i < src.Length; i++)
            {
                if (src[i].X < minX) minX = src[i].X;
                if (src[i].Y < minY) minY = src[i].Y;
            }

            var pieces = new TrackShapePiece[src.Length];
            for (int i = 0; i < src.Length; i++)
            {
                var p = src[i];
                var pieceType = new TrackPieceShape(p.ShapeId);
                if (!pieceCatalog.TryGet(pieceType, out _))
                    throw new AuthoredTrackConversionException(
                        $"authored track '{data.Id}' piece #{i} references unknown piece '{p.ShapeId}'");

                // Out-of-range variant indices fall back to Default rather than throwing —
                // a stale save with a removed variant should still load.
                byte variantByte = (p.VariantIndex < 0 || p.VariantIndex > byte.MaxValue)
                    ? (byte)0
                    : (byte)p.VariantIndex;

                pieces[i] = new TrackShapePiece(
                    pieceType,
                    new GridOffset(p.X - minX, p.Y - minY),
                    (TrackDirection)(p.Facing & 7),
                    new TrackPieceVariantId(variantByte));
            }

            var sourceId = string.IsNullOrEmpty(data.Id)
                ? Guid.NewGuid().ToString("N")
                : data.Id;
            var id = new TrackShapeId(IdPrefix + sourceId);
            var name = string.IsNullOrEmpty(data.DisplayName) ? sourceId : data.DisplayName;
            var shape = new TrackShape(id, name, pieces);

            ValidateNoOverlap(pieceCatalog, shape);
            return shape;
        }

        private static void ValidateNoOverlap(ITrackPieceCatalog pieces, TrackShape shape)
        {
            var anchor = new GridPosition(0, 0);
            var occupied = new HashSet<GridPosition>();
            for (int i = 0; i < shape.Pieces.Count; i++)
            {
                var p = shape.Pieces[i];
                if (!pieces.TryGet(p.PieceType, out var def)) continue;
                var tileOrigin = p.Offset.Apply(anchor, TrackDirection.North);
                var resolvedFacing = p.ResolveFacing(TrackDirection.North);
                foreach (var t in def.Footprint.Tiles(tileOrigin, resolvedFacing))
                {
                    if (!occupied.Add(t))
                        throw new AuthoredTrackConversionException(
                            $"authored track '{shape.Id}' piece #{i} ({p.PieceType.Id}) overlaps tile {t}");
                }
            }
        }
    }

    /// <summary>
    /// Pulls every authored track off disk (via <see cref="AuthoredTrackJsonStore"/>),
    /// converts each through <see cref="AuthoredTrackToShapeConverter.Convert"/>, and
    /// registers the result into a runtime <see cref="ITrackShapeCatalog"/>. Per-id
    /// failures (corrupt JSON, unknown piece type, overlap) log a warning and skip
    /// — one bad file can't take the scenario down.
    /// <para>
    /// Used by <c>MouseShapePlacementScenario</c> only — that scenario flips to
    /// authored-only as the explicit player-curated card pool. Other scenarios
    /// (training generators, realistic loop tests) keep
    /// <see cref="TrackShapeCatalogSeeder"/> as their recipe pool. Don't copy this
    /// loader into those without revisiting the design.
    /// </para>
    /// </summary>
    public static class AuthoredCardLoader
    {
        /// <summary>
        /// Returns the number of authored cards successfully registered.
        /// </summary>
        public static int LoadAllInto(ITrackShapeCatalog catalog, ITrackPieceCatalog pieceCatalog)
        {
            if (catalog == null || pieceCatalog == null) return 0;
            var ids = AuthoredTrackJsonStore.ListIds();
            int registered = 0;
            for (int i = 0; i < ids.Count; i++)
            {
                var id = ids[i];
                try
                {
                    var data = AuthoredTrackJsonStore.Load(id);
                    if (data == null) continue;
                    var shape = AuthoredTrackToShapeConverter.Convert(data, pieceCatalog);
                    if (catalog.Has(shape.Id)) continue;
                    catalog.Register(shape);
                    registered++;
                }
                catch (Exception e)
                {
                    Debug.LogWarning(
                        $"[AuthoredCardLoader] Skipped authored track '{id}': {e.Message}");
                }
            }
            return registered;
        }
    }

}
