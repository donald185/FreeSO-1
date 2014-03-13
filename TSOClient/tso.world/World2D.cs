﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Xna.Framework.Graphics;
using tso.world.model;
using Microsoft.Xna.Framework;
using TSO.Common.utils;
using TSO.Common.rendering.framework;
using TSOClient.Code.Utils;
using tso.world.components;

namespace tso.world
{
    /// <summary>
    /// Handles rendering the 2D world
    /// </summary>
    public class World2D
    {
        public static SurfaceFormat[] BUFFER_SURFACE_FORMATS = new SurfaceFormat[] {
            SurfaceFormat.Color,
            SurfaceFormat.Color,
            /** Depth buffer must be single surface format for precision reasons **/
            SurfaceFormat.Single,
            SurfaceFormat.Color,
            /** Object ID buffer **/
            SurfaceFormat.Single
        };

        public static int NUM_2D_BUFFERS = 5;
        public static int BUFFER_STATIC_FLOOR = 0;
        public static int BUFFER_STATIC_OBJECTS_PIXEL = 1;
        public static int BUFFER_STATIC_OBJECTS_DEPTH = 2;
        public static int BUFFER_STATIC_TERRAIN = 3;
        public static int BUFFER_OBJID = 4;


        private Blueprint Blueprint;
        private Dictionary<WorldComponent, WorldObjectRenderInfo> RenderInfo = new Dictionary<WorldComponent, WorldObjectRenderInfo>();

        private Texture2D StaticTerrain;
        private Texture2D StaticFloor;
        private Texture2D StaticObjects;
        private Texture2D StaticObjectsDepth;

        public bool DrawCenterPoint = true;
        private Texture2D CenterPixel;


        public void Initialize(_3DLayer layer)
        {
            CenterPixel = TextureUtils.TextureFromColor(layer.Device, Color.Purple, 4, 4);
        }

        public void Init(Blueprint blueprint){
            this.Blueprint = blueprint;
            //this.TileInfo = new WorldTileRenderingInfo[blueprint.Width * blueprint.Height];
        }

        private WorldObjectRenderInfo GetRenderInfo(WorldComponent component){
            return ((ObjectComponent)component).renderInfo;

            /*if (RenderInfo.ContainsKey(component))
            {
                return RenderInfo[component];
            }
            var info = new WorldObjectRenderInfo();
            RenderInfo[component] = info;
            return info;*/
        }

        public short GetObjectIDAtScreenPos(int x, int y, GraphicsDevice gd, WorldState state)
        {
            /** Draw all objects to a texture as their IDs **/
            var occupiedTiles = Blueprint.GetOccupiedTiles(state.Rotation);
            var pxOffset = state.WorldSpace.GetScreenOffset();
            pxOffset.X -= x;
            pxOffset.Y -= y;
            var _2d = state._2D;
            Promise<Texture2D> bufferTexture = null;
            state._2D.OBJIDMode = true;
            using (var buffer = state._2D.WithBuffer(BUFFER_OBJID, ref bufferTexture))
            {
                
                while (buffer.NextPass())
                {
                    foreach (var tile in occupiedTiles)
                    {

                        /** Objects **/
                        if ((tile.Type & BlueprintOccupiedTileType.OBJECT) == BlueprintOccupiedTileType.OBJECT)
                        {
                            var objects = Blueprint.GetObjects(tile.TileX, tile.TileY);
                            foreach (var obj in objects.Objects)
                            {
                                var tilePosition = obj.Position;
                                _2d.OffsetPixel(state.WorldSpace.GetScreenFromTile(tilePosition) + pxOffset);
                                _2d.OffsetTile(tilePosition);
                                _2d.SetObjID(obj.ObjectID);
                                obj.Draw(gd, state);
                            }
                        }
                    }
                }
                
            }
            state._2D.OBJIDMode = false;

            var tex = bufferTexture.Get();
            Single[] data = new float[1];
            tex.GetData<Single>(data);
            return (short)Math.Round(data[0]*65535f);
        }

        /// <summary>
        /// Prep work before screen is painted
        /// </summary>
        /// <param name="gd"></param>
        /// <param name="state"></param>
        public void PreDraw(GraphicsDevice gd, WorldState state){
            var damage = Blueprint.Damage;
            var _2d = state._2D;

            /**
             * Tasks:
             *  If scroll, zoom or rotation has changed, redraw all static layers
             *  If architecture has changed, redraw appropriate static layer
             *  If there is a new object in the static layer, redraw the static layer
             *  If an objects in the static layer has changed, redraw the static layer and move the object to the dynamic layer
             *  If wall visibility has changed, redraw wall layer (should think about how this works with breakthrough wall mode
             */

            var redrawTerrain = StaticTerrain == null;
            var redrawStaticObjects = false;
            var redrawFloors = false;

            WorldObjectRenderInfo info = null;

            foreach (var item in damage){
                switch (item.Type){
                    case BlueprintDamageType.ROTATE:
                    case BlueprintDamageType.ZOOM:
                    case BlueprintDamageType.SCROLL:
                        redrawFloors = true;
                        redrawStaticObjects = true;
                        redrawTerrain = true;
                        break;
                    case BlueprintDamageType.OBJECT_MOVE:
                        /** Redraw if its in static layer **/
                        info = GetRenderInfo(item.Component);
                        if (info.Layer == WorldObjectRenderLayer.STATIC){
                            redrawStaticObjects = true;
                        }
                        break;
                    case BlueprintDamageType.OBJECT_GRAPHIC_CHANGE:
                        /** Redraw if its in static layer **/
                        info = GetRenderInfo(item.Component);
                        if (info.Layer == WorldObjectRenderLayer.STATIC){
                            redrawStaticObjects = true;
                            info.Layer = WorldObjectRenderLayer.DYNAMIC;
                        }
                        break;
                    case BlueprintDamageType.FLOOR_CHANGED:
                        redrawFloors = true;
                        break;
                }
            }
            damage.Clear();

            var occupiedTiles = Blueprint.GetOccupiedTiles(state.Rotation);
            var pxOffset = state.WorldSpace.GetScreenOffset();

            if (redrawTerrain)
            {
                Promise<Texture2D> bufferTexture = null;
                using (var buffer = state._2D.WithBuffer(BUFFER_STATIC_TERRAIN, ref bufferTexture)){
                    while (buffer.NextPass()){
                        Blueprint.Terrain.Draw(gd, state);
                    }
                }
                StaticTerrain = bufferTexture.Get();
            }

            if (redrawFloors){
                Promise<Texture2D> bufferTexture = null;
                using (var buffer = state._2D.WithBuffer(BUFFER_STATIC_FLOOR, ref bufferTexture))
                {
                    while (buffer.NextPass())
                    {
                        foreach (var tile in occupiedTiles)
                        {
                            var tilePosition = new Vector3(tile.TileX, tile.TileY, 0.0f);
                            _2d.OffsetPixel(state.WorldSpace.GetScreenFromTile(tilePosition) + pxOffset);
                            _2d.OffsetTile(tilePosition);
                            _2d.SetObjID(0);

                            if ((tile.Type & BlueprintOccupiedTileType.FLOOR) == BlueprintOccupiedTileType.FLOOR)
                            {
                                var floor = Blueprint.GetFloor(tile.TileX, tile.TileY);
                                floor.Draw(gd, state);
                            }
                        }
                    }
                }
                StaticFloor = bufferTexture.Get();
                //StaticFloor.Save("C:\\floor.png", ImageFileFormat.Png);
            }

            if (redrawStaticObjects){
                /** Draw static objects to a texture **/
                Promise<Texture2D> bufferTexture = null;
                Promise<Texture2D> depthTexture = null;
                using (var buffer = state._2D.WithBuffer(BUFFER_STATIC_OBJECTS_PIXEL, ref bufferTexture, BUFFER_STATIC_OBJECTS_DEPTH, ref depthTexture))
                {
                    while (buffer.NextPass())
                    {
                        foreach (var tile in occupiedTiles)
                        {

                            /** Objects **/
                            if ((tile.Type & BlueprintOccupiedTileType.OBJECT) == BlueprintOccupiedTileType.OBJECT)
                            {
                                var objects = Blueprint.GetObjects(tile.TileX, tile.TileY);
                                foreach (var obj in objects.Objects)
                                {
                                    var renderInfo = GetRenderInfo(obj);
                                    if (renderInfo.Layer == WorldObjectRenderLayer.STATIC)
                                    {
                                        var tilePosition = obj.Position;
                                        _2d.OffsetPixel(state.WorldSpace.GetScreenFromTile(tilePosition) + pxOffset);
                                        _2d.OffsetTile(tilePosition);
                                        _2d.SetObjID(obj.ObjectID);
                                        obj.Draw(gd, state);
                                    }
                                }
                            }
                        }
                    }
                }

                StaticObjects = bufferTexture.Get();
                StaticObjectsDepth = depthTexture.Get();
                //StaticObjects.Save("C:\\static.png", ImageFileFormat.Png);
            }

            //foreach (var tile in damage){
            //    _2d.OffsetPixel(new Vector2(0.0f, 0.0f));
            //    _2d.OffsetTile(new Vector3(tile.TileX, tile.TileY, 0.0f));
            //    /** Redraw tile **/
            //    Promise<Texture2D> bufferTexture = null;
            //    using (state._2D.WithBuffer(ref bufferTexture)){
            //        /** Render objects on the tile **/
            //        //if ((tile.Type & BlueprintOccupiedTileType.OBJECT) == BlueprintOccupiedTileType.OBJECT){
            //            var objects = Blueprint.GetObjects(tile.TileX, tile.TileY);
            //            foreach (var obj in objects.Objects)
            //            {
            //                obj.Draw(gd, state);
            //            }
            //        //}
            //    }

            //    var offset = (tile.TileY * Blueprint.Width) + tile.TileX;
            //    TileInfo[offset].Pixel = bufferTexture.Get();
            //}
        }

        /// <summary>
        /// Paint to screen
        /// </summary>
        /// <param name="gd"></param>
        /// <param name="state"></param>
        public void Draw(GraphicsDevice gd, WorldState state){

            var _2d = state._2D;
            /**
             * Draw static layers
             */
            _2d.OffsetPixel(Vector2.Zero);

            if (StaticTerrain != null){
                _2d.DrawBasic(StaticTerrain, Vector2.Zero);
            }
            if (StaticFloor != null){
                _2d.DrawBasic(StaticFloor, Vector2.Zero);
            }
            if (StaticObjects != null && StaticObjectsDepth != null)
            {
                _2d.DrawBasicRestoreDepth(StaticObjects, StaticObjectsDepth, Vector2.Zero);
            }

            /**
             * Draw dynamic objects. If an object has been static for X frames move it back into the static layer
             */

            var occupiedTiles = Blueprint.GetOccupiedTiles(state.Rotation);
            var pxOffset = state.WorldSpace.GetScreenOffset();

            foreach (var tile in occupiedTiles)
            {

                /** Objects **/
                if ((tile.Type & BlueprintOccupiedTileType.OBJECT) == BlueprintOccupiedTileType.OBJECT)
                {
                    var objects = Blueprint.GetObjects(tile.TileX, tile.TileY);
                    foreach (var obj in objects.Objects)
                    {
                        var renderInfo = GetRenderInfo(obj);
                        if (renderInfo.Layer == WorldObjectRenderLayer.DYNAMIC)
                        {
                            var tilePosition = obj.Position;
                            _2d.OffsetPixel(state.WorldSpace.GetScreenFromTile(tilePosition) + pxOffset);
                            _2d.OffsetTile(tilePosition);
                            _2d.SetObjID(obj.ObjectID);
                            obj.Draw(gd, state);
                        }
                    }
                }
            }

            /** Center point **/
            if (DrawCenterPoint)
            {
                _2d.OffsetPixel(Vector2.Zero);
                _2d.DrawBasic(CenterPixel, new Vector2((gd.Viewport.Width / 2.0f)-2, (gd.Viewport.Height / 2.0f)-2));
            }


            //var pxOffset = new Vector2(512.0f, -512.0f);
            //var _2d = state._2D;

            //foreach (var tile in Blueprint.GetOccupiedTiles(state.Rotation)){
            //    var tilePosition = new Vector3(tile.TileX, tile.TileY, 0.0f);
            //    _2d.OffsetPixel(state.WorldSpace.GetScreenFromTileWithoutScroll(tilePosition) + pxOffset);
            //    _2d.OffsetTile(tilePosition);

            //    /** Do i have a texture for this tile? **/
            //    var tileInfo = this.TileInfo[(tile.TileY * Blueprint.Width) + tile.TileX];
            //    if (tileInfo.Pixel != null){
            //        _2d.DrawBasic(tileInfo.Pixel, Vector2.Zero);
            //    }
            //}
        }
    }

    public class WorldObjectRenderInfo
    {
        public WorldObjectRenderLayer Layer = WorldObjectRenderLayer.STATIC;
    }

    public enum WorldObjectRenderLayer
    {
        STATIC,
        DYNAMIC
    }

    public struct WorldTileRenderingInfo
    {
        public bool Dirty;
        public Texture2D Pixel;
        public Texture2D ZBuffer;
    }
}