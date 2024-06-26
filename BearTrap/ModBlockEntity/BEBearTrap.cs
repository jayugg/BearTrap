#nullable enable
using System;
using System.Collections.Generic;
using System.Text;
using BearTrap.Util;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace BearTrap.ModBlockEntity
{
    public enum EnumTrapState
    {
        Closed,
        Open,
        Baited,
        Destroyed
    }

    public class BlockEntityBearTrap : BlockEntityDisplay, IAnimalFoodSource, IMountable
    {
        protected ICoreServerAPI Sapi;
        private readonly object _lock = new object();
        private InventoryGeneric _inv = new(1, null, null);
        public override InventoryBase Inventory => _inv;
        public override string InventoryClassName => "beartrap";
        public override int DisplayedItems => TrapState == EnumTrapState.Baited ? 1 : 0;
        public override string AttributeTransformCode => "beartrap";

        private string? _destroyedByLangCode;
        
        private int MaxDamage => ((ModBlock.BearTrap)Block).MaxDamage;

        private int _damage;
        public int Damage 
        { 
            get => _damage;
            set => _damage = Math.Min(value, MaxDamage); // Ensure Damage never exceeds MaxDamage
        }
        
        private Dictionary<EnumTrapState, AssetLocation> _shapeByState;
        

        public Vec3d Position => Pos.ToVec3d().Add(0.5, 0.25, 0.5);
        public string Type => _inv.Empty ? "nothing" : "food";
        
        float _rotationYDeg;
        float[] _rotMat;

        public float RotationYDeg
        {
            get { return _rotationYDeg; }
            set { 
                _rotationYDeg = value;
                _rotMat = Matrixf.Create().Translate(0.5f, 0, 0.5f).RotateYDeg(_rotationYDeg - 90).Translate(-0.5f, 0, -0.5f).Values;
            }
        }

        public string MetalVariant => ((ModBlock.BearTrap)Block).MetalVariant;

        private EnumTrapState _trapState;

        public EnumTrapState TrapState
        {
            get { return _trapState;}
            set
            {
                _trapState = value;
                if (value != EnumTrapState.Baited) _inv[0].Itemstack = null;
                if (value != EnumTrapState.Closed) UnmountEntity("openTrap");
                MarkDirty(true);
            }
        }
        
        public float SnapDamage => ((ModBlock.BearTrap)Block).SnapDamage;

        public override void Initialize(ICoreAPI api)
        {
            base.Initialize(api);
            _inv.LateInitialize("beartrap-" + Pos, api);
            
            Sapi = api as ICoreServerAPI;
            if (api.Side != EnumAppSide.Client)
            {
                Sapi?.ModLoader.GetModSystem<POIRegistry>().AddPOI(this);
            }
            
            var shapeByStateString = Block.Attributes?["shapeBy"].AsObject<Dictionary<string, string>>();
            Dictionary<EnumTrapState, AssetLocation> shapeAssetLocations = new Dictionary<EnumTrapState, AssetLocation>();

            if (shapeByStateString != null)
            {
                foreach (var pair in shapeByStateString)
                {
                    if (Enum.TryParse(pair.Key, true, out EnumTrapState state))
                    {
                        shapeAssetLocations[state] = AssetLocation.Create(pair.Value, Block.Code.Domain).WithPathPrefixOnce("shapes/").WithPathAppendixOnce(".json");
                    }
                }
            }

            _shapeByState = shapeAssetLocations;
            
            this._controls.OnAction = this.OnControls;
            
            EntityAgent? entityAgent;
            if (this._mountedByPlayerUid == null)
            {
                entityAgent = api.World.GetEntityById(this._mountedByEntityId) as EntityAgent;
            }
            else
            {
                IPlayer player = api.World.PlayerByUid(this._mountedByPlayerUid);
                entityAgent = player?.Entity;
            }
            MountEntity(entityAgent, "init");
            api.World.RegisterGameTickListener(SlowTick, 50);
        }
        
        private void SlowTick(float deltaTime)
        {
            if (TrapState != EnumTrapState.Closed) return;
            if (MountedBy is { Alive: false })
            {
                UnmountEntity("dead");
            }
            if (MountedBy == null) return;
            if (MountedBy.AnimManager != null && MountedBy.AnimManager.IsAnimationActive("walk", "spring", "run", "move"))
            {
                DamageEntityAndTrap();
            }
        }

        private void SetDestroyed()
        {
            TrapState = EnumTrapState.Destroyed;
            Damage = MaxDamage;
            Api.World.PlaySoundAt(new AssetLocation("game:sounds/effect/anvilhit3"), Pos.X + 0.5,
                Pos.Y + 0.25, Pos.Z + 0.5, null, true, 16);

            UnmountEntity("destroyed");
        }
        
        public void MountEntity(EntityAgent? entityAgent, string? dsc = null) 
        {
            if (entityAgent == null) return;
            entityAgent.TryMount(this);
            MountedBy = entityAgent;
            if (MountedBy is EntityPlayer entityPlayer)
            {
                entityPlayer.Stats.Set("walkspeed", Core.Modid + "trapped", -1);
            }
            this._destroyedByLangCode = "prefixandcreature-" + entityAgent.Code.Path.Replace("-", "");
        }

        public void UnmountEntity(String dsc = null)
        {
            if (MountedBy is EntityPlayer entityPlayer)
            {
                entityPlayer.Stats.Remove("walkspeed", Core.Modid + "trapped");
            }
            this.MountedBy?.TryUnmount();
        }
        
        public bool Interact(IPlayer player, BlockSelection blockSel)
        {
            switch (TrapState)
            {
                case EnumTrapState.Destroyed:
                    return true;
                case EnumTrapState.Closed when player.Entity.Controls.Sneak:
                    TrapState = EnumTrapState.Open;
                    return true;
                case EnumTrapState.Open when _inv[0].Empty:
                    return TryReadyTrap(player);
                // Damage players if they attempt to touch the trap without sneaking
                case EnumTrapState.Open when !(player.Entity.Controls.Sneak || player.Entity.Controls.FloorSitting):
                case EnumTrapState.Baited when !(player.Entity.Controls.Sneak || player.Entity.Controls.FloorSitting):
                    SnapClosed(player.Entity);
                    return true;
                case EnumTrapState.Baited when _inv[0].Itemstack != null:
                {
                    if (!player.InventoryManager.TryGiveItemstack(_inv[0].Itemstack))
                    {
                        Api.World.SpawnItemEntity(_inv[0].Itemstack, Pos.ToVec3d().Add(0.5, 0.2, 0.5));
                    }

                    TrapState = EnumTrapState.Open;
                    return true;
                }
                default:
                    return false;
            }
        }

        private bool TryReadyTrap(IPlayer player)
        {
            var heldSlot = player.InventoryManager.ActiveHotbarSlot;
            if (heldSlot.Empty) return false;

            var collobj = heldSlot.Itemstack.Collectible;
            if (!heldSlot.Empty && (collobj.NutritionProps != null || collobj.Attributes?["foodTags"].Exists == true))
            {
                _inv[0].Itemstack = heldSlot.TakeOut(1);
                TrapState = EnumTrapState.Baited;
                heldSlot.MarkDirty();
                return true;
            }
            return false;
        }
        
        public bool IsSuitableFor(Entity entity, CreatureDiet diet)
        {
            if (TrapState != EnumTrapState.Baited) return false;
            if (diet.FoodTags.Length == 0) return entity.IsCreature;
            bool dietMatches = diet.Matches(_inv[0].Itemstack);
            return dietMatches;
        }

        public float ConsumeOnePortion(Entity entity)
        {
            Sapi.Event.EnqueueMainThreadTask(() => SnapClosed(entity), "trapanimal");
            return 1f;
        }

        public void SnapClosed(Entity entity)
        {
            if (TrapState is EnumTrapState.Destroyed or EnumTrapState.Closed) return;
            if (entity.IsCreature)
            {
                TrapState = EnumTrapState.Closed;
                DamageEntity(entity, SnapDamage);
                if (entity is EntityAgent entityAgent)
                {
                    if (entityAgent.MountedOn == null) MountEntity(entityAgent, "snapclosed");
                }
                _inv[0].Itemstack = null;
            }

            Damage += 1;
            Api.World.PlaySoundAt(new AssetLocation("game:sounds/effect/anvilhit1"), Pos.X + 0.5, Pos.Y + 0.25,
                Pos.Z + 0.5, null, true, 16);
        }
        
        private void DamageEntity(Entity entity, float damage)
        {
            if (!entity.HasBehavior<EntityBehaviorHealth>()) { return;}

            var shouldRelease = false;
            if (entity.GetBehavior<EntityBehaviorHealth>().Health - damage <= 0)
            {
                entity.WatchedAttributes.SetString("deathByEntityLangCode", "prefixandcreature-" + Block.Code.FirstCodePart());
                entity.WatchedAttributes.SetString("deathByEntity", Block.Code.FirstCodePart());
                shouldRelease = true;
            }
            entity.ReceiveDamage(new DamageSource()
                {
                    Source = EnumDamageSource.Block,
                    SourceBlock = this.Block,
                    Type = EnumDamageType.PiercingAttack,
                    SourcePos = MountPosition.AsBlockPos.ToVec3d()
                },
                damage: damage);
            if (shouldRelease) UnmountEntity();
        }
        
        public override void OnBlockBroken(IPlayer byPlayer = null)
        {
            base.OnBlockBroken(byPlayer);
            if (Api.Side == EnumAppSide.Server)
            {
                Api.ModLoader.GetModSystem<POIRegistry>().RemovePOI(this);
            }
        }

        public override void OnBlockRemoved()
        {
            base.OnBlockRemoved();
            if (Api.Side == EnumAppSide.Server)
            {
                Api.ModLoader.GetModSystem<POIRegistry>().RemovePOI(this);
                UnmountEntity("blockremoved");
            }
        }

        public override void OnBlockUnloaded()
        {
            base.OnBlockUnloaded();

            if (Api?.Side == EnumAppSide.Server)
            {
                Api.ModLoader.GetModSystem<POIRegistry>().RemovePOI(this);
            }
        }
        
        public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldForResolving)
        {
            base.FromTreeAttributes(tree, worldForResolving);
            RotationYDeg = tree.GetFloat("rotationYDeg");
            Damage = tree.GetInt("damage");
            if (Damage > MaxDamage - 1)
            {
                SetDestroyed();
            }
            else
            {
                TrapState = (EnumTrapState)tree.GetInt("trapState");
            }
            this._mountedByEntityId = tree.GetLong("mountedByEntityId");
            this._mountedByPlayerUid = tree.GetString("mountedByPlayerUid");
            
            this._destroyedByLangCode = tree.GetString("destroyedByLangCode");

            // Do this last
            RedrawAfterReceivingTreeAttributes(worldForResolving);     // Redraw on client after we have completed receiving the update from server
        }


        public override void ToTreeAttributes(ITreeAttribute tree)
        {
            base.ToTreeAttributes(tree);
            tree.SetFloat("rotationYDeg", _rotationYDeg);
            tree.SetInt("damage", _damage);
            tree.SetInt("trapState", (int)TrapState);
            tree.SetLong("mountedByEntityId", this._mountedByEntityId);
            tree.SetString("mountedByPlayerUid", this._mountedByPlayerUid);
            tree.SetString("destroyedByLangCode", _destroyedByLangCode);
        }

        
        public override void GetBlockInfo(IPlayer forPlayer, StringBuilder dsc)
        {
            if (TrapState == EnumTrapState.Destroyed)
            {
                if (_destroyedByLangCode != null)
                {
                    dsc.Append(Lang.Get(Core.Modid + ":info-beartrap-destroyedby") + Lang.Get(_destroyedByLangCode));
                    return;
                }
                dsc.Append(Lang.Get(Core.Modid + ":info-beartrap-destroyed"));
                return;
            }
            dsc.Append("Durability: " + (MaxDamage - Damage) + "/" + (MaxDamage) + "\n");
            if (TrapState == EnumTrapState.Baited)
            {
                dsc.Append(BlockEntityShelf.PerishableInfoCompact(Api, _inv[0], 0));
            }
            dsc.Append(TrapState);
        }

        protected override float[][] genTransformationMatrices()
        {
            tfMatrices = new float[1][];

            for (int i = 0; i < 1; i++)
            {
                tfMatrices[i] =
                    new Matrixf()
                    .Translate(0.5f, 0.1f, 0.5f)
                    .Scale(0.75f, 0.75f, 0.75f)
                    .Translate(-0.5f, 0, -0.5f)
                    .Values
                ;
            }

            return tfMatrices;
        }
        
        public MeshData GetOrCreateMesh(AssetLocation loc, ITexPositionSource texSource = null)
        {
            return ObjectCacheUtil.GetOrCreate(Api, "bearTrap-" + MetalVariant + loc + (texSource == null ? "-d" : "-t"), () =>
            {
                var shape = Api.Assets.Get<Shape>(loc);
                if (texSource == null)
                {
                    texSource = new ShapeTextureSource(capi, shape, loc.ToShortString());
                }
                
                var block = Api.World.BlockAccessor.GetBlock(Pos);
                ((ICoreClientAPI)Api).Tesselator.TesselateShape(block, Api.Assets.Get<Shape>(loc), out var meshdata);
                return meshdata;
            });
        }

        public MeshData GetCurrentMesh(ITexPositionSource texSource = null)
        {
            if (TrapState == EnumTrapState.Baited) return GetOrCreateMesh(_shapeByState[EnumTrapState.Open], texSource);
            return GetOrCreateMesh(_shapeByState[TrapState], texSource);
        }
        
        public override bool OnTesselation(ITerrainMeshPool mesher, ITesselatorAPI tessThreadTesselator)
        {

            bool skip = base.OnTesselation(mesher, tessThreadTesselator);
            if (!skip)
            {
                mesher.AddMeshData(GetCurrentMesh(this), _rotMat);
            }
            return true;
        }
        
        // IMountable

        public void MountableToTreeAttributes(TreeAttribute tree)
        {
            tree.SetString("className", Core.Modid + "beartrap");
            tree.SetInt("posx", this.Pos.X);
            tree.SetInt("posy", this.Pos.InternalY);
            tree.SetInt("posz", this.Pos.Z);
        }

        public void DidUnmount(EntityAgent entityAgent)
        {
            this.MountedBy = null;
            this._mountedByEntityId = 0L;
            this._mountedByPlayerUid = null;
            this.LocalEyePos = null;
        }

        public void DidMount(EntityAgent entityAgent)
        {
            if (this.MountedBy == entityAgent)
                return;
            this.MountedBy = entityAgent;
            this._mountedByPlayerUid = entityAgent is EntityPlayer entityPlayer ? entityPlayer.PlayerUID : null;
            this._mountedByEntityId = this.MountedBy.EntityId;
            this.LocalEyePos = entityAgent.LocalEyePos.ToVec3f();
        }
        
        private EntityControls _controls = new EntityControls();
        public EntityControls Controls => this._controls;
        public EntityAgent MountedBy;
        public bool CanControl => false;
        Entity IMountable.MountedBy => this.MountedBy;
        public IMountableSupplier MountSupplier => null;
        private EntityPos _mountPos = new EntityPos();
        private long _mountedByEntityId;
        private string? _mountedByPlayerUid;
        public EntityPos MountPosition
        {
            get
            {
                this._mountPos.SetPos(this.Pos);
                this._mountPos.Add(0.5f, 0f, 0.5f);
                return this._mountPos;
            }
        }
        
        private void OnControls(EnumEntityAction action, bool on, ref EnumHandling handled)
        {
            this._controls.StopAllMovement();
            if (on && action is not (EnumEntityAction.Backward or EnumEntityAction.Forward or EnumEntityAction.Right
                or EnumEntityAction.Left or EnumEntityAction.Up)) return;
            if (MountedBy != null)
            {
                DamageEntityAndTrap();
            }
            handled = EnumHandling.PreventSubsequent;
        }

        private double lastTrapDamageTime;
        private double trapDamageCooldown = 0.02;
        
        private void DamageEntityAndTrap()
        {
            double currentTime = Api.World.Calendar.TotalHours;
            if (currentTime - lastTrapDamageTime >= trapDamageCooldown)
            {
                lastTrapDamageTime = currentTime;
                Api.World.PlaySoundAt(new AssetLocation("game:sounds/effect/anvilhit1"), Pos.X + 0.5,
                    Pos.Y + 0.25, Pos.Z + 0.5, null, true, 16);
                Damage += 1;
                MarkDirty();
                if (Damage > MaxDamage - 1)
                {
                    SetDestroyed();
                }
            }
            DamageEntity(MountedBy, SnapDamage * 0.1f);
            var tiredHours = MountedBy is EntityPlayer ? 1f : 4f;
            BehaviorUtil.AddTiredness(MountedBy,tiredHours);
        }

        public EnumMountAngleMode AngleMode => EnumMountAngleMode.Unaffected;
        public string SuggestedAnimation => "stand";
        public Vec3f? LocalEyePos { get; private set; }
    }
}