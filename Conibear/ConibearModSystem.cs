﻿using Conibear.Block;
using Conibear.BlockEntity;
using Conibear.EntityBehaviour;
using Vintagestory.API.Client;
using Vintagestory.API.Server;
using Vintagestory.API.Config;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

namespace Conibear;

public class ConibearModSystem : ModSystem
{
    // Called on server and client
    // Useful for registering block/entity classes on both sides
    public override void Start(ICoreAPI api)
    {
        api.Logger.Notification("Hello from template mod: " + api.Side);
        api.RegisterBlockClass(Mod.Info.ModID + ".conibear", typeof(ConibearTrap));
        api.RegisterBlockEntityClass(Mod.Info.ModID + ".blockentityconibear", typeof(BlockEntityConibearTrap));
        api.RegisterEntityBehaviorClass(Mod.Info.ModID + ".trapped", typeof(TrappedBehaviour));
        api.Event.OnEntitySpawn += OnEntitySpawn;
    }

    private void OnEntitySpawn(Entity entity)
    {
        if (entity.IsCreature)
        {
            var trappedBehaviour = new TrappedBehaviour(entity);
            entity.AddBehavior(trappedBehaviour);
        }
    }

    public override void StartServerSide(ICoreServerAPI api)
    {
        api.Logger.Notification("Hello from template mod server side: " + Lang.Get("conibear:hello"));
    }

    public override void StartClientSide(ICoreClientAPI api)
    {
        api.Logger.Notification("Hello from template mod client side: " + Lang.Get("conibear:hello"));
    }
}