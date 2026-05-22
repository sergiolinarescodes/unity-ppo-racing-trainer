using Unidad.Core.Factory;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.Ghost.Presentation
{
    internal sealed class GhostCarPresenter : IGhostCarPresenter
    {
        private const float DefaultCubeSize = 0.5f;
        private static readonly Color BaseColor = new(0.40f, 0.85f, 1.00f, 0.55f); // cool teal, translucent

        private readonly IGameObjectFactory _factory;
        private GameObject _root;
        private MeshRenderer _renderer;
        private Material _mat;

        public GhostCarPresenter(IGameObjectFactory factory)
        {
            _factory = factory;
        }

        public Transform Root => _root != null ? _root.transform : null;

        private void EnsureBuilt()
        {
            if (_root != null) return;

            _root = _factory.CreateEmpty("GhostCar");
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = "GhostBody";
            cube.transform.SetParent(_root.transform, false);
            cube.transform.localScale = Vector3.one * DefaultCubeSize;

            // Strip collider — visual only.
            var col = cube.GetComponent<Collider>();
            if (col != null) Object.Destroy(col);

            _renderer = cube.GetComponent<MeshRenderer>();
            var shader = Shader.Find("Universal Render Pipeline/Lit")
                         ?? Shader.Find("Standard")
                         ?? Shader.Find("Hidden/InternalErrorShader");
            _mat = new Material(shader) { name = "GhostMaterial", color = BaseColor };
            // Enable transparency for URP Lit + Standard.
            _mat.SetFloat("_Surface", 1f);         // URP: 0 opaque, 1 transparent
            _mat.SetFloat("_Mode", 3f);            // Standard: 3 = transparent
            _mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            _mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            _mat.SetInt("_ZWrite", 0);
            _mat.DisableKeyword("_ALPHATEST_ON");
            _mat.EnableKeyword("_ALPHABLEND_ON");
            _mat.renderQueue = 3000;
            _renderer.sharedMaterial = _mat;
        }

        public void Show(Vector3 worldPos, float headingRad, float bodyLeanRad, float alpha = 1f)
        {
            EnsureBuilt();
            _root.SetActive(true);
            _root.transform.position = worldPos;
            // Body lean = roll around forward axis; heading = yaw.
            _root.transform.rotation = Quaternion.Euler(0f, headingRad * Mathf.Rad2Deg, bodyLeanRad * Mathf.Rad2Deg);
            if (_mat != null)
            {
                var c = BaseColor;
                c.a *= Mathf.Clamp01(alpha);
                _mat.color = c;
            }
        }

        public void Hide()
        {
            if (_root == null) return;
            _root.SetActive(false);
        }
    }
}
