using UnityEngine;

// Stands in for the port's T2DCompat.Atlas (tools/downgrade/atlas/compat/Atlas.cs) in the mesh test: SpriteBatch
// takes a frame's page from Atlas.Get, and here the pages are the test's synthetic atlases.
namespace T2DCompat
{
    public static class Atlas
    {
        public static Texture2D[] Pages;

        public static Texture2D Get(int page) { return Pages[page]; }
    }
}
