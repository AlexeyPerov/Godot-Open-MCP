#if TOOLS
#nullable enable
using System;
using System.Globalization;
using System.Text;
using Godot;
using GodotOpenMcp.Bridge.Runtime.Screenshot;
using SMath = GodotOpenMcp.Bridge.Runtime.Screenshot.ScreenshotMath;

namespace GodotOpenMcp.Bridge.Editor
{
    /// <summary>
    /// Render service for the three screenshot tools (P4.8). Editor-only (<c>#if TOOLS</c>): every
    /// method touches Godot editor / rendering APIs (<c>EditorInterface</c>, <c>SubViewport</c>,
    /// <c>RenderingServer</c>, <c>Camera3D</c>/<c>Camera2D</c>, <c>Aabb</c>) and runs on the editor
    /// main thread.
    ///
    /// <para>
    /// <b>Fidelity: adapt.</b> The rendering approach is lifted from the Godot-MCP behavior
    /// reference (<c>Tool_Screenshot.Viewport.cs</c> / <c>Camera.cs</c> / <c>Isolated.cs</c>) — the
    /// canonical Godot-flavored capture: editor viewport readback, off-screen <c>SubViewport</c>
    /// sharing the source world (camera tool), and isolated <c>OwnWorld3D</c> capture (isolated
    /// tool). The Unity Open MCP screenshot service uses Unity's RenderTexture / Camera.Render
    /// path which has no direct Godot equivalent — Godot uses <c>SubViewport</c> +
    /// <c>RenderingServer.ForceDraw</c>. The pure framing math (<see cref="ScreenshotMath"/>) is
    /// the shared authority.
    /// </para>
    ///
    /// <para>
    /// <b>Read-only + cleanup.</b> Every temporary <c>SubViewport</c>/<c>Camera3D</c>/light is
    /// owned by the calling handler and freed in <c>finally</c> on success, error, and timeout.
    /// No persistent file is written — the encoded PNG is returned in-memory to the handler, which
    /// wraps it in the bridge image envelope.
    /// </para>
    /// </summary>
    internal static class ScreenshotService
    {
        // --- capture result envelope ------------------------------------------------

        /// <summary>
        /// Outcome of a render attempt. On success, <see cref="Png"/> is the encoded PNG bytes and
        /// <see cref="Error"/> is null; on failure, <see cref="Code"/>/<see cref="Error"/> carry a
        /// stable error code + message for the handler to surface. The handler also reads
        /// <see cref="Width"/>/<see cref="Height"/> (post-clamp final dimensions) and
        /// <see cref="Clamped"/> (whether the transport limit downscale was applied).
        /// </summary>
        internal sealed class CaptureResult
        {
            internal bool Success => Png != null && Code == null;
            internal byte[]? Png { get; set; }
            internal int Width { get; set; }
            internal int Height { get; set; }
            internal bool Clamped { get; set; }
            internal string? Code { get; set; }
            internal string? Error { get; set; }

            internal static CaptureResult Ok(byte[] png, int width, int height, bool clamped) => new()
            {
                Png = png,
                Width = width,
                Height = height,
                Clamped = clamped,
            };

            internal static CaptureResult Fail(string code, string message) => new()
            {
                Code = code,
                Error = message,
            };
        }

        // --- viewport capture ------------------------------------------------------

        /// <summary>
        /// Capture the active editor 2D or 3D viewport. <paramref name="normalizedMode"/> must be
        /// <c>"2d"</c> or <c>"3d"</c> (the handler normalizes). Returns a
        /// <see cref="CaptureResult"/>; never throws.
        /// </summary>
        internal static CaptureResult CaptureViewport(string normalizedMode)
        {
            Viewport? viewport;
            try
            {
                viewport = normalizedMode == "2d"
                    ? EditorInterface.Singleton.GetEditorViewport2D()
                    : EditorInterface.Singleton.GetEditorViewport3D(0);
            }
            catch (Exception e)
            {
                return CaptureResult.Fail("viewport_unavailable",
                    $"Could not access the editor {normalizedMode} viewport: {e.Message}");
            }

            if (viewport == null || !GodotObject.IsInstanceValid(viewport))
                return CaptureResult.Fail("viewport_unavailable",
                    $"Editor {normalizedMode} viewport is unavailable (no open {normalizedMode} view?).");

            return EncodeViewportPng(viewport, flipY: false);
        }

        // --- camera capture --------------------------------------------------------

        /// <summary>
        /// Capture from a <c>Camera2D</c> or <c>Camera3D</c> in an off-screen
        /// <c>SubViewport</c>. <paramref name="camera"/> must already be resolved by the handler.
        /// Returns a <see cref="CaptureResult"/>; never throws.
        /// </summary>
        internal static CaptureResult CaptureCamera(Node camera, int width, int height)
        {
            if (!SMath.ValidateDimensions(width, height, out var dimError))
                return CaptureResult.Fail("invalid_dimensions", dimError);

            var (clampedW, clampedH) = SMath.ClampToTransportLimit(width, height);
            var clamped = clampedW != width || clampedH != height;

            if (camera is Camera3D cam3D)
                return CaptureCamera3D(cam3D, clampedW, clampedH, clamped);
            if (camera is Camera2D cam2D)
                return CaptureCamera2D(cam2D, clampedW, clampedH, clamped);
            return CaptureResult.Fail("invalid_camera_node",
                $"Expected a Camera3D or Camera2D; got {camera.GetClass()}.");
        }

        static CaptureResult CaptureCamera3D(Camera3D source, int width, int height, bool clamped)
        {
            World3D? world;
            try { world = source.GetWorld3D(); }
            catch (Exception e)
            {
                return CaptureResult.Fail("render_unavailable",
                    $"Camera3D '{source.Name}' has no World3D; cannot capture: {e.Message}");
            }
            if (world == null)
                return CaptureResult.Fail("render_unavailable",
                    $"Camera3D '{source.Name}' is not inside a scene with a World3D; cannot capture.");

            SubViewport? sub = null;
            Camera3D? clone = null;
            try
            {
                sub = new SubViewport
                {
                    Size = new Vector2I(width, height),
                    RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
                    World3D = world,
                };
                clone = new Camera3D
                {
                    GlobalTransform = source.GlobalTransform,
                    Projection = source.Projection,
                    Fov = source.Fov,
                    Size = source.Size,
                    Near = source.Near,
                    Far = source.Far,
                    KeepAspect = source.KeepAspect,
                    Current = true,
                };
                sub.AddChild(clone);
                return RenderSubViewportToPng(sub, width, height, clamped);
            }
            catch (Exception e)
            {
                return CaptureResult.Fail("render_unavailable",
                    $"Camera3D capture failed: {e.Message}");
            }
            finally
            {
                FreeOffscreenViewport(sub);
            }
        }

        static CaptureResult CaptureCamera2D(Camera2D source, int width, int height, bool clamped)
        {
            Viewport? sourceViewport;
            World2D? world2D;
            try
            {
                sourceViewport = source.GetViewport();
                world2D = sourceViewport?.GetWorld2D();
            }
            catch (Exception e)
            {
                return CaptureResult.Fail("render_unavailable",
                    $"Camera2D '{source.Name}' viewport/world lookup failed: {e.Message}");
            }
            if (world2D == null)
                return CaptureResult.Fail("render_unavailable",
                    $"Camera2D '{source.Name}' is not inside a viewport with a World2D; cannot capture.");

            SubViewport? sub = null;
            try
            {
                sub = new SubViewport
                {
                    Size = new Vector2I(width, height),
                    RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
                    World2D = world2D,
                };
                // Reproduce the source camera framing via the canvas transform.
                var zoom = source.Zoom;
                if (zoom.X == 0f) zoom.X = 1f;
                if (zoom.Y == 0f) zoom.Y = 1f;
                Vector2 center;
                try { center = source.GetScreenCenterPosition(); }
                catch { center = source.GlobalPosition; }
                var canvasTransform = new Transform2D(
                    0f, zoom, 0f,
                    new Vector2(width, height) * 0.5f - center * zoom);
                sub.CanvasTransform = canvasTransform;
                return RenderSubViewportToPng(sub, width, height, clamped);
            }
            catch (Exception e)
            {
                return CaptureResult.Fail("render_unavailable",
                    $"Camera2D capture failed: {e.Message}");
            }
            finally
            {
                FreeOffscreenViewport(sub);
            }
        }

        // --- isolated capture ------------------------------------------------------

        /// <summary>
        /// Capture a <c>Node3D</c> in an isolated world (no source-scene scripts run). Computes the
        /// combined AABB over <c>VisualInstance3D</c> descendants, frames a fresh camera from the
        /// requested view using <see cref="ScreenshotMath"/>, and renders to a square
        /// <c>SubViewport</c>. Returns a <see cref="CaptureResult"/> with extra bounds metadata
        /// available via <paramref name="boundsMetadata"/> (center + size) on success; never
        /// throws.
        /// </summary>
        internal static CaptureResult CaptureIsolated(
            Node3D target,
            ScreenshotMath.View view,
            string background, // "solid_color" | "transparent"
            string backgroundColorHex,
            float fieldOfView,
            float nearClipPlane,
            float farClipPlane,
            float padding,
            int resolution,
            out string? boundsMetadata)
        {
            boundsMetadata = null;

            // Validate the framing math before touching the tree.
            if (!SMath.ValidateDimensions(resolution, resolution, out var dimError))
                return CaptureResult.Fail("invalid_dimensions", dimError);
            if (!SMath.ValidateFraming(fieldOfView, nearClipPlane, farClipPlane, padding, out var framingError))
                return CaptureResult.Fail("invalid_dimensions", framingError);

            var bgTransparent = background == "transparent";
            var (resW, resH) = SMath.ClampToTransportLimit(resolution, resolution);
            var clamped = resW != resolution || resH != resolution;

            SubViewport? sub = null;
            try
            {
                sub = new SubViewport
                {
                    Size = new Vector2I(resW, resH),
                    RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
                    OwnWorld3D = true,
                    TransparentBg = bgTransparent,
                };

                // Duplicate WITHOUT scripts so the clone's _EnterTree/_Ready never fires — a static
                // off-screen render needs no behavior, and running target scripts would be a side
                // effect that contradicts the read-only contract.
                Node3D? clone;
                try
                {
                    clone = (Node3D)target.Duplicate((int)(Node.DuplicateFlags.Signals | Node.DuplicateFlags.Groups));
                    clone.Transform = Transform3D.Identity;
                }
                catch (Exception e)
                {
                    return CaptureResult.Fail("render_unavailable",
                        $"Isolated node duplication failed: {e.Message}");
                }
                sub.AddChild(clone);

                var bounds = ComputeAabb(clone);
                var radius = bounds.Size.Length() * 0.5f;
                var distance = SMath.ComputeCameraDistance(radius, fieldOfView, padding);
                var (near, far) = SMath.BracketClipPlanes(distance, radius, nearClipPlane, farClipPlane);

                SMath.GetViewDirectionAndUp(view,
                    out var dirX, out var dirY, out var dirZ,
                    out var upX, out var upY, out var upZ);
                var dirVec = new Vector3(dirX, dirY, dirZ);
                var upVec = new Vector3(upX, upY, upZ);
                var center = bounds.GetCenter();
                var camPos = center + dirVec * distance;

                var camera = new Camera3D
                {
                    Projection = Camera3D.ProjectionType.Perspective,
                    Fov = fieldOfView,
                    Near = near,
                    Far = far,
                    Current = true,
                };
                sub.AddChild(camera);
                try
                {
                    camera.LookAtFromPosition(camPos, center, upVec);
                }
                catch (Exception e)
                {
                    return CaptureResult.Fail("degenerate_geometry",
                        $"Camera framing failed for this geometry (view={view}): {e.Message}");
                }

                var light = new DirectionalLight3D
                {
                    LightEnergy = 1.0f,
                    LightColor = Colors.White,
                };
                sub.AddChild(light);
                try { light.LookAtFromPosition(camPos, center, upVec); }
                catch { /* non-fatal — light orientation best-effort */ }

                // SolidColor background requires a WorldEnvironment (TransparentBg handles transparent).
                if (!bgTransparent)
                {
                    if (SMath.TryParseHtmlColor(backgroundColorHex, out var bg))
                    {
                        var env = new WorldEnvironment
                        {
                            Environment = new Godot.Environment
                            {
                                BackgroundMode = Godot.Environment.BGMode.Color,
                                BackgroundColor = new Color(bg.R, bg.G, bg.B, bg.A),
                            },
                        };
                        sub.AddChild(env);
                    }
                    else
                    {
                        return CaptureResult.Fail("invalid_capture_mode",
                            $"background_color '{backgroundColorHex}' is not a valid hex color (#RGB / #RRGGBB / #RRGGBBAA).");
                    }
                }

                // Emit the bounds metadata for the handler's envelope.
                var meta = new StringBuilder(128);
                meta.Append(",\"bounds\":{");
                meta.Append("\"center\":[").Append(center.X.ToString("R", CultureInfo.InvariantCulture))
                    .Append(',').Append(center.Y.ToString("R", CultureInfo.InvariantCulture))
                    .Append(',').Append(center.Z.ToString("R", CultureInfo.InvariantCulture)).Append("],");
                meta.Append("\"size\":[").Append(bounds.Size.X.ToString("R", CultureInfo.InvariantCulture))
                    .Append(',').Append(bounds.Size.Y.ToString("R", CultureInfo.InvariantCulture))
                    .Append(',').Append(bounds.Size.Z.ToString("R", CultureInfo.InvariantCulture)).Append("],");
                meta.Append("\"radius\":").Append(radius.ToString("R", CultureInfo.InvariantCulture)).Append(',');
                meta.Append("\"cameraDistance\":").Append(distance.ToString("R", CultureInfo.InvariantCulture)).Append(',');
                meta.Append("\"near\":").Append(near.ToString("R", CultureInfo.InvariantCulture)).Append(',');
                meta.Append("\"far\":").Append(far.ToString("R", CultureInfo.InvariantCulture)).Append(',');
                meta.Append("\"usedFallbackBounds\":").Append(radius < 0.051f ? "true" : "false");
                meta.Append('}');
                boundsMetadata = meta.ToString();

                return RenderSubViewportToPng(sub, resW, resH, clamped);
            }
            catch (Exception e)
            {
                return CaptureResult.Fail("render_unavailable",
                    $"Isolated capture failed: {e.Message}");
            }
            finally
            {
                FreeOffscreenViewport(sub);
            }
        }

        // --- shared encode / render helpers ---------------------------------------

        /// <summary>
        /// Read the <c>ViewportTexture</c>, validate the image, apply orientation correction,
        /// clamp to the transport limit, and encode PNG. <paramref name="flipY"/> defaults false
        /// because Godot's <c>GetImage</c> already accounts for the API origin on GPU-rendered
        /// targets (rows arrive upright). Returns a <see cref="CaptureResult"/>; never throws.
        /// </summary>
        static CaptureResult EncodeViewportPng(Viewport viewport, bool flipY)
        {
            Texture2D? texture;
            try { texture = viewport.GetTexture(); }
            catch (Exception e)
            {
                return CaptureResult.Fail("render_unavailable",
                    $"Viewport texture lookup failed: {e.Message}");
            }
            if (texture == null)
                return CaptureResult.Fail("render_unavailable",
                    "Viewport has no render texture (rendering device unavailable? running --headless?).");

            Image? image;
            try { image = texture.GetImage(); }
            catch (Exception e)
            {
                return CaptureResult.Fail("render_unavailable",
                    $"Viewport texture readback failed: {e.Message}");
            }
            if (image == null || image.IsEmpty() || image.GetWidth() <= 0 || image.GetHeight() <= 0)
                return CaptureResult.Fail("empty_image",
                    "Viewport texture read back an empty image. This is common under --headless or " +
                    "when no GPU/rendering device is available.");

            var origW = image.GetWidth();
            var origH = image.GetHeight();
            var clamped = false;

            if (flipY)
            {
                try { image.FlipY(); }
                catch { /* non-fatal */ }
            }

            var (w, h) = SMath.ClampToTransportLimit(origW, origH);
            if (w != origW || h != origH)
            {
                clamped = true;
                try { image.Resize(w, h, Image.Interpolation.Bilinear); }
                catch { /* non-fatal — keep the original if resize fails */ }
            }

            byte[] png;
            try { png = image.SavePngToBuffer(); }
            catch (Exception e)
            {
                return CaptureResult.Fail("png_encode_failed",
                    $"PNG encoding failed: {e.Message}");
            }
            if (png == null || png.Length == 0 || !SMath.HasPngSignature(png))
                return CaptureResult.Fail("png_encode_failed",
                    "PNG encoding produced an empty or invalid buffer.");

            if (png.Length > SMath.MaxEncodedBytes)
                return CaptureResult.Fail("image_too_large",
                    $"Encoded PNG is {png.Length} bytes, exceeds the {SMath.MaxEncodedBytes}-byte transport " +
                    "ceiling even after dimension clamping. Reduce the capture resolution.");

            return CaptureResult.Ok(png, w, h, clamped);
        }

        /// <summary>
        /// Host a <c>SubViewport</c> in the editor tree, force the draw frames needed for the
        /// off-screen target to land, read back the PNG, and un-host in <c>finally</c>. The caller
        /// owns lifetime (<see cref="FreeOffscreenViewport"/>); this helper only hosts/un-hosts so
        /// a throw during construction is still cleaned up by the caller's finally.
        /// </summary>
        static CaptureResult RenderSubViewportToPng(SubViewport sub, int width, int height, bool clamped)
        {
            Control? host = null;
            try
            {
                host = EditorInterface.Singleton.GetBaseControl();
                host.AddChild(sub);
                // Two force-draws: empirically a single draw can read back before the off-screen
                // target has content (clear color only). Always-update + two force-draws lets the
                // scene land before the synchronous readback.
                RenderingServer.ForceDraw();
                RenderingServer.ForceDraw();
                return EncodeViewportPng(sub, flipY: false);
            }
            finally
            {
                if (sub != null && GodotObject.IsInstanceValid(sub) && sub.GetParent() != null)
                    sub.GetParent().RemoveChild(sub);
            }
        }

        /// <summary>
        /// Idempotently free an off-screen <c>SubViewport</c>: null-guard, un-parent, then
        /// <c>QueueFree</c> the whole subtree (descendants — clone camera, clone node, light,
        /// environment — are freed together).
        /// </summary>
        static void FreeOffscreenViewport(SubViewport? sub)
        {
            if (sub == null || !GodotObject.IsInstanceValid(sub)) return;
            try
            {
                if (sub.GetParent() != null)
                    sub.GetParent().RemoveChild(sub);
                sub.QueueFree();
            }
            catch
            {
                // best-effort cleanup — never throw out of finally
            }
        }

        /// <summary>
        /// Compute the combined AABB over every <c>VisualInstance3D</c> descendant of
        /// <paramref name="root"/>, transforming each local AABB to world via the instance's
        /// global transform. An empty/degenerate result falls back to a 0.1-unit box at the origin
        /// so framing math stays finite.
        /// </summary>
        static Aabb ComputeAabb(Node3D root)
        {
            var initialised = false;
            var bounds = new Aabb();
            foreach (var vi in CollectVisualInstances(root))
            {
                Aabb local;
                try { local = vi.GetAabb(); }
                catch { continue; }
                var worldAabb = vi.GlobalTransform * local;
                if (!initialised) { bounds = worldAabb; initialised = true; }
                else bounds = bounds.Merge(worldAabb);
            }
            if (!initialised || bounds.Size.LengthSquared() < 1e-8f)
                bounds = new Aabb(Vector3.Zero, Vector3.One * 0.1f);
            return bounds;
        }

        static System.Collections.Generic.IEnumerable<VisualInstance3D> CollectVisualInstances(Node node)
        {
            if (node is VisualInstance3D vi) yield return vi;
            foreach (var child in node.GetChildren(includeInternal: false))
                foreach (var nested in CollectVisualInstances(child))
                    yield return nested;
        }
    }
}
#endif
