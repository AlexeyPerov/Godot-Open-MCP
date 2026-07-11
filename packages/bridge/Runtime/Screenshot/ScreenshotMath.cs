#nullable enable
using System;

namespace GodotOpenMcp.Bridge.Runtime.Screenshot
{
    /// <summary>
    /// Pure screenshot math (P4.8) — dimension clamping, camera framing, clip-plane bracketing,
    /// hex-color parsing, and view-direction tables. Godot-free (no <c>using Godot;</c>, no
    /// <c>#if TOOLS</c>) so every routine is unit-testable in the binary-less xUnit host.
    ///
    /// <para>
    /// <b>Fidelity: adapt.</b> The math is lifted from the Godot-MCP behavior reference
    /// (<c>addons/godot_mcp/Runtime/Tools/ScreenshotMath.cs</c>) because it is the canonical
    /// Godot-flavored framing math (Godot AABB/Vector3 conventions, Godot Camera3D projection
    /// semantics). The Unity Open MCP screenshot service uses Unity's own bounds/camera math;
    /// that math is not directly portable because Godot's coordinate system and clip-plane
    /// defaults differ. The constants and the bracket/loosen semantics are preserved verbatim.
    /// </para>
    /// </summary>
    public static class ScreenshotMath
    {
        // --- dimension limits -------------------------------------------------------

        /// <summary>
        /// Hard transport cap on the longest edge of any captured image (px). True 4K headroom;
        /// model vision downsamples to ~1568 px anyway. Any image exceeding this is downscaled
        /// (aspect preserved) before encoding.
        /// </summary>
        public const int MaxScreenshotDimension = 3840;

        /// <summary>Smallest caller-requested dimension accepted (px). Below this is a contract
        /// violation, not a clamp target.</summary>
        public const int MinDimension = 1;

        /// <summary>Largest caller-requested dimension accepted (px) before clamping. A request
        /// above this still succeeds but is clamped to <see cref="MaxScreenshotDimension"/>.</summary>
        public const int MaxDimension = 16384;

        // --- optics limits ----------------------------------------------------------

        /// <summary>Smallest accepted field of view (degrees).</summary>
        public const float MinFieldOfView = 1f;

        /// <summary>Largest accepted field of view (degrees). 180° is degenerate for perspective;
        /// we stop just short.</summary>
        public const float MaxFieldOfView = 179f;

        /// <summary>Smallest accepted framing padding multiplier.</summary>
        public const float MinPadding = 0.01f;

        /// <summary>Largest accepted framing padding multiplier.</summary>
        public const float MaxPadding = 100f;

        /// <summary>Upper bound on the near clip plane the caller may request (m).</summary>
        public const float MaxNearClip = 1000f;

        /// <summary>Upper bound on the far clip plane the caller may request (m).</summary>
        public const float MaxFarClip = 1e6f;

        // --- byte limit -------------------------------------------------------------

        /// <summary>
        /// Hard cap on the encoded PNG byte length transported through the bridge. An encoded PNG
        /// that still exceeds this after dimension clamping is rejected with <c>image_too_large</c>.
        /// Sized to keep the base64 envelope well under typical HTTP / MCP message limits.
        /// </summary>
        public const int MaxEncodedBytes = 8 * 1024 * 1024;

        // --- dimension validation + clamping ---------------------------------------

        /// <summary>
        /// Bounds-check a caller-requested width/height. Returns false (and sets
        /// <paramref name="error"/>) when either is below <see cref="MinDimension"/> or above
        /// <see cref="MaxDimension"/>. Does NOT clamp — the caller clamps via
        /// <see cref="ClampToTransportLimit"/> after validation succeeds.
        /// </summary>
        public static bool ValidateDimensions(int width, int height, out string? error)
        {
            if (width < MinDimension || height < MinDimension)
            {
                error = $"Dimensions must be >= {MinDimension}; got width={width}, height={height}.";
                return false;
            }
            if (width > MaxDimension || height > MaxDimension)
            {
                error = $"Dimensions must be <= {MaxDimension}; got width={width}, height={height}.";
                return false;
            }
            error = null;
            return true;
        }

        /// <summary>
        /// Clamp a width/height so the longest edge is at most <see cref="MaxScreenshotDimension"/>,
        /// preserving aspect ratio. Returns the input unchanged when already within the cap. Each
        /// axis floors at 1 px so an extreme-aspect image never collapses the short edge to 0.
        /// </summary>
        public static (int width, int height) ClampToTransportLimit(int width, int height)
        {
            var longest = Math.Max(width, height);
            if (longest <= MaxScreenshotDimension) return (width, height);

            var scale = (float)MaxScreenshotDimension / longest;
            var w = (int)Math.Round(width * scale);
            var h = (int)Math.Round(height * scale);
            return (Math.Max(1, w), Math.Max(1, h));
        }

        // --- framing validation -----------------------------------------------------

        /// <summary>
        /// Validate the isolated-capture framing parameters. Rejects non-finite (NaN/Infinity)
        /// values and out-of-range fov/padding/clip values. Returns false (and sets
        /// <paramref name="error"/>) on any violation.
        /// </summary>
        public static bool ValidateFraming(
            float fieldOfView, float nearClip, float farClip, float padding, out string? error)
        {
            if (!IsFinite(fieldOfView) || fieldOfView < MinFieldOfView || fieldOfView > MaxFieldOfView)
            {
                error = $"field_of_view must be a finite value in [{MinFieldOfView}, {MaxFieldOfView}]; got {fieldOfView}.";
                return false;
            }
            if (!IsFinite(padding) || padding < MinPadding || padding > MaxPadding)
            {
                error = $"padding must be a finite value in [{MinPadding}, {MaxPadding}]; got {padding}.";
                return false;
            }
            if (!IsFinite(nearClip) || nearClip <= 0f || nearClip > MaxNearClip)
            {
                error = $"near_clip_plane must be finite, > 0, and <= {MaxNearClip}; got {nearClip}.";
                return false;
            }
            if (!IsFinite(farClip) || farClip <= nearClip || farClip > MaxFarClip)
            {
                error = $"far_clip_plane must be finite, > near_clip_plane, and <= {MaxFarClip}; got {farClip}.";
                return false;
            }
            error = null;
            return true;
        }

        /// <summary>True when <paramref name="v"/> is neither NaN nor Infinity.</summary>
        public static bool IsFinite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);

        // --- view direction table ---------------------------------------------------

        /// <summary>
        /// One of the six orthographic views the isolated capture can render from. The
        /// <c>(-Z)</c>/<c>(+Z)</c> annotations document the world axis the camera looks along,
        /// matching Godot's convention.
        /// </summary>
        public enum View
        {
            /// <summary>Looking along -Z (toward the camera's back is +Z).</summary>
            Front,
            /// <summary>Looking along +Z.</summary>
            Back,
            /// <summary>Looking along -X.</summary>
            Left,
            /// <summary>Looking along +X.</summary>
            Right,
            /// <summary>Looking along +Y (down at the XZ plane).</summary>
            Top,
            /// <summary>Looking along -Y (up at the XZ plane).</summary>
            Bottom,
        }

        /// <summary>
        /// Resolve a view name string (case-insensitive) to a <see cref="View"/>. Returns false
        /// for an unknown name. The default (no/invalid input) is <see cref="View.Front"/>.
        /// </summary>
        public static View ParseView(string? name) => TryParseView(name, out var v) ? v : View.Front;

        /// <summary>Case-insensitive parse of a view name. Returns false for an unknown name.</summary>
        public static bool TryParseView(string? name, out View view)
        {
            if (!string.IsNullOrEmpty(name))
            {
                switch (name.Trim().ToLowerInvariant())
                {
                    case "front": view = View.Front; return true;
                    case "back": view = View.Back; return true;
                    case "left": view = View.Left; return true;
                    case "right": view = View.Right; return true;
                    case "top": view = View.Top; return true;
                    case "bottom": view = View.Bottom; return true;
                }
            }
            view = View.Front;
            return false;
        }

        /// <summary>
        /// The world-space direction (normalized) the camera should look along for the given view,
        /// and the up vector to keep <c>LookAt</c> non-degenerate. Returned as plain float triples
        /// (not Godot <c>Vector3</c>) to keep this helper Godot-free. Top/Bottom use up = +Z
        /// (forward) so the up vector is never parallel to the direction.
        /// </summary>
        public static void GetViewDirectionAndUp(View view, out float dirX, out float dirY, out float dirZ,
            out float upX, out float upY, out float upZ)
        {
            switch (view)
            {
                case View.Back:
                    dirX = 0f; dirY = 0f; dirZ = 1f; upX = 0f; upY = 1f; upZ = 0f; return;
                case View.Left:
                    dirX = -1f; dirY = 0f; dirZ = 0f; upX = 0f; upY = 1f; upZ = 0f; return;
                case View.Right:
                    dirX = 1f; dirY = 0f; dirZ = 0f; upX = 0f; upY = 1f; upZ = 0f; return;
                case View.Top:
                    dirX = 0f; dirY = 1f; dirZ = 0f; upX = 0f; upY = 0f; upZ = 1f; return;
                case View.Bottom:
                    dirX = 0f; dirY = -1f; dirZ = 0f; upX = 0f; upY = 0f; upZ = 1f; return;
                case View.Front:
                default:
                    dirX = 0f; dirY = 0f; dirZ = -1f; upX = 0f; upY = 1f; upZ = 0f; return;
            }
        }

        // --- camera distance + clip planes -----------------------------------------

        /// <summary>
        /// Compute the camera distance from the bounds center so a sphere of
        /// <paramref name="boundsRadius"/> fits the vertical half-FOV, scaled by
        /// <paramref name="padding"/>. Trig: <c>distance = (radius * padding) / sin(fov/2)</c>.
        /// For fov=60°, half-angle=30°, sin=0.5, so distance = 2·radius·padding. A tiny/zero
        /// radius is floored to 0.05 so point/empty targets still produce a finite positive
        /// distance.
        /// </summary>
        public static float ComputeCameraDistance(float boundsRadius, float fieldOfViewDegrees, float padding)
        {
            if (boundsRadius < 0.0001f) boundsRadius = 0.05f;
            var fovRad = fieldOfViewDegrees * 0.5f * (float)(Math.PI / 180.0);
            var sin = (float)Math.Sin(fovRad);
            if (sin < 1e-6f) sin = 1e-6f;
            return (boundsRadius * padding) / sin;
        }

        /// <summary>
        /// Adjust the caller's clip planes so the framed object's depth span
        /// [<c>cameraDistance - span</c>, <c>cameraDistance + span</c>] is guaranteed inside
        /// [<c>near</c>, <c>far</c>]. Only <b>loosens</b> the planes (near moves closer, far moves
        /// farther); never tightens. <c>span</c> = <c>radius * 1.05</c> (silhouette margin). Near
        /// is floored positive and <c>far &gt; near</c> is guaranteed for degenerate inputs.
        /// </summary>
        public static (float near, float far) BracketClipPlanes(
            float cameraDistance, float boundsRadius, float userNear, float userFar)
        {
            const float k = 1.05f;
            var span = Math.Max(0f, boundsRadius) * k;
            var frontFace = Math.Max(1e-4f, cameraDistance - span);
            var backFace = cameraDistance + span;

            var near = Math.Min(userNear, frontFace);
            var far = Math.Max(userFar, backFace);
            if (near <= 0f) near = 1e-4f;
            if (far <= near) far = near + Math.Max(span * 2f, 1e-3f);
            return (near, far);
        }

        // --- hex color parsing ------------------------------------------------------

        /// <summary>Parsed RGBA color, normalized to [0,1]. Godot-free so the parser stays
        /// unit-testable without the Godot binary.</summary>
        public readonly struct ColorF
        {
            /// <summary>Red in [0,1].</summary>
            public readonly float R;
            /// <summary>Green in [0,1].</summary>
            public readonly float G;
            /// <summary>Blue in [0,1].</summary>
            public readonly float B;
            /// <summary>Alpha in [0,1]. Defaults to 1 when the input had no alpha channel.</summary>
            public readonly float A;

            /// <summary>Construct from normalized components.</summary>
            public ColorF(float r, float g, float b, float a = 1f)
            {
                R = r; G = g; B = b; A = a;
            }
        }

        /// <summary>
        /// Parse an HTML/CSS-style hex color (#RGB, #RRGGBB, or #RRGGBBAA) into normalized
        /// [0,1] floats. Leading <c>#</c> is optional. Returns false for an invalid length or a
        /// non-hex digit. Does NOT construct a Godot <c>Color</c> — the editor handler does that.
        /// </summary>
        public static bool TryParseHtmlColor(string? hex, out ColorF color)
        {
            color = default;
            if (string.IsNullOrWhiteSpace(hex)) return false;

            var s = hex!.Trim();
            if (s.StartsWith("#", StringComparison.Ordinal)) s = s.Substring(1);
            if (s.Length != 3 && s.Length != 6 && s.Length != 8) return false;

            foreach (var c in s)
                if (!IsHexDigit(c)) return false;

            float r, g, b, a = 1f;
            if (s.Length == 3)
            {
                r = Hex1(s[0]); g = Hex1(s[1]); b = Hex1(s[2]);
            }
            else if (s.Length == 6)
            {
                r = Hex2(s, 0); g = Hex2(s, 2); b = Hex2(s, 4);
            }
            else // length == 8
            {
                r = Hex2(s, 0); g = Hex2(s, 2); b = Hex2(s, 4); a = Hex2(s, 6);
            }

            color = new ColorF(r, g, b, a);
            return true;
        }

        static bool IsHexDigit(char c) =>
            (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

        /// <summary>One hex digit → [0,1] by doubling the nibble (e.g. <c>F</c> → 255/255 = 1).</summary>
        static float Hex1(char c) => HexVal(c) * (1f / 15f);

        /// <summary>Two hex digits starting at <paramref name="offset"/> → [0,1].</summary>
        static float Hex2(string s, int offset) =>
            (HexVal(s[offset]) * 16f + HexVal(s[offset + 1])) * (1f / 255f);

        static int HexVal(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            return c - 'A' + 10;
        }

        // --- PNG validation ---------------------------------------------------------

        /// <summary>The PNG file signature (first 8 bytes): 137 80 78 71 13 10 26 10.</summary>
        public static readonly byte[] PngSignature = { 137, 80, 78, 71, 13, 10, 26, 10 };

        /// <summary>
        /// True when <paramref name="bytes"/> is non-null, longer than the 8-byte signature, and
        /// starts with the PNG signature. Does NOT parse IHDR — the bridge enforces dimension
        /// limits before encoding, not after.
        /// </summary>
        public static bool HasPngSignature(byte[]? bytes)
        {
            if (bytes == null || bytes.Length < PngSignature.Length) return false;
            for (int i = 0; i < PngSignature.Length; i++)
                if (bytes[i] != PngSignature[i]) return false;
            return true;
        }
    }
}
