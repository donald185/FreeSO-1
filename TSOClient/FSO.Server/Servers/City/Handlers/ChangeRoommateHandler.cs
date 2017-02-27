﻿using FSO.Common.DataService;
using FSO.Common.DataService.Model;
using FSO.Server.Database.DA;
using FSO.Server.Database.DA.Lots;
using FSO.Server.Database.DA.Roommates;
using FSO.Server.Framework.Aries;
using FSO.Server.Framework.Voltron;
using FSO.Server.Protocol.Electron.Model;
using FSO.Server.Protocol.Electron.Packets;
using FSO.Server.Protocol.Gluon.Packets;
using FSO.Server.Servers.City.Domain;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FSO.Server.Servers.City.Handlers
{
    public class ChangeRoommateHandler
    {
        private IDAFactory DAFactory;
        private CityServerContext Context;
        private ISessions Sessions;
        private IDataService DataService;
        private LotServerPicker LotServers;
        private LotAllocations Lots;

        public ChangeRoommateHandler(ISessions sessions, IDAFactory da, CityServerContext context, IDataService dataService, LotServerPicker lotServers, LotAllocations lots)
        {
            this.Sessions = sessions;
            this.DAFactory = da;
            this.Context = context;
            this.DataService = dataService;
            this.LotServers = lotServers;
            this.Lots = lots;
        }

        private void Status(IVoltronSession session, ChangeRoommateResponseStatus status)
        {
            session.Write(new ChangeRoommateResponse { Type = status });
        }

        public async void Handle(IVoltronSession session, ChangeRoommateRequest packet)
        {
            try
            {
                if (session.IsAnonymous) return;
                using (var da = DAFactory.Get())
                {
                    if (packet.Type == ChangeRoommateType.POLL)
                    {
                        var lots = da.Roommates.GetAvatarsLots(session.AvatarId);
                        foreach (var lot in lots)
                        {
                            if (lot.is_pending == 1)
                            {
                                var lotdb = da.Lots.Get(lot.lot_id);
                                if (lotdb == null) return;
                                session.Write(new ChangeRoommateRequest
                                {
                                    Type = ChangeRoommateType.INVITE,
                                    AvatarId = lotdb.owner_id,
                                    LotLocation = lotdb.location
                                });
                            }
                        }
                    }
                    else if (packet.Type == ChangeRoommateType.ACCEPT)
                    {
                        var lot = da.Lots.GetByLocation(Context.ShardId, packet.LotLocation);
                        if (lot == null) { Status(session, ChangeRoommateResponseStatus.LOT_DOESNT_EXIST); return; }
                        if (da.Roommates.AcceptRoommateRequest(session.AvatarId, lot.lot_id))
                        {
                            var lotDS = await DataService.Get<FSO.Common.DataService.Model.Lot>(packet.LotLocation);
                            if (lotDS != null) lotDS.Lot_RoommateVec = lotDS.Lot_RoommateVec.Add(session.AvatarId);

                            var lotOwned = da.LotClaims.GetByLotID(lot.lot_id);
                            if (lotOwned != null)
                            {
                                var lotServer = LotServers.GetLotServerSession(lotOwned.owner);
                                if (lotServer != null)
                                {
                                    //immediately notify lot of new roommate
                                    lotServer.Write(new NotifyLotRoommateChange()
                                    {
                                        AvatarId = session.AvatarId,
                                        LotId = lot.lot_id,
                                        Change = Protocol.Gluon.Model.ChangeType.ADD_ROOMMATE
                                    });
                                }
                            }

                            var avatar = await DataService.Get<Avatar>(session.AvatarId);
                            if (avatar != null) avatar.Avatar_LotGridXY = packet.LotLocation;
                            Status(session, ChangeRoommateResponseStatus.ACCEPT_SUCCESS); return;
                        }
                        else
                        {
                            Status(session, ChangeRoommateResponseStatus.NO_INVITE_PENDING); return;
                        }
                    }
                    else if (packet.Type == ChangeRoommateType.DECLINE)
                    {
                        var lot = da.Lots.GetByLocation(Context.ShardId, packet.LotLocation);
                        if (lot == null) { Status(session, ChangeRoommateResponseStatus.LOT_DOESNT_EXIST); return; }
                        if (da.Roommates.DeclineRoommateRequest(session.AvatarId, lot.lot_id))
                        {
                            Status(session, ChangeRoommateResponseStatus.DECLINE_SUCCESS); return;
                        }
                        else
                        {
                            Status(session, ChangeRoommateResponseStatus.NO_INVITE_PENDING); return;
                        }
                    }
                    else
                    {
                        //verify that requester is definitely a roommate in the target lot

                        var ownedLot = da.Lots.GetByOwner(session.AvatarId);
                        var myLots = da.Roommates.GetAvatarsLots(session.AvatarId);

                        if (packet.Type == ChangeRoommateType.INVITE)
                        {
                            //is invitee roommate somewhere else? count lot roommates and check for max
                            var targ = da.Avatars.Get(packet.AvatarId);
                            if (targ == null)
                            {
                                Status(session, ChangeRoommateResponseStatus.UNKNOWN);
                            }
                            var targLots = da.Roommates.GetAvatarsLots(packet.AvatarId);
                            if (targLots.Count > 0)
                            {
                                Status(session, ChangeRoommateResponseStatus.ROOMIE_ELSEWHERE); //request already pending or otherwise
                                return;
                            }
                            var lotr = myLots.FirstOrDefault();
                            DbLot lot = null;
                            if (lotr != null) lot = da.Lots.Get(lotr.lot_id);
                            if (lotr == null || lot == null)
                            {
                                Status(session, ChangeRoommateResponseStatus.LOT_DOESNT_EXIST); //what??
                                return;
                            }
                            if (lot.owner_id != session.AvatarId) //only an owner can add roommates
                            {
                                Status(session, ChangeRoommateResponseStatus.YOU_ARE_NOT_OWNER);
                                return;
                            }
                            var myLotRoomies = da.Roommates.GetLotRoommates(lotr.lot_id);
                            if (myLotRoomies.Count >= 8)
                            {
                                //if pending roommates put us over, cancel some of them.
                                //assume first is oldest request
                                var pending = myLotRoomies.FirstOrDefault(x => x.is_pending == 1);
                                if (pending == null)
                                {
                                    Status(session, ChangeRoommateResponseStatus.TOO_MANY_ROOMMATES);
                                    return;
                                }
                                else
                                {
                                    da.Roommates.DeclineRoommateRequest(pending.avatar_id, pending.lot_id);
                                }
                            }
                            //create roommate request in database

                            if (!da.Roommates.Create(new DbRoommate
                            {
                                avatar_id = packet.AvatarId,
                                lot_id = lotr.lot_id,
                                is_pending = 1,
                                permissions_level = 0
                            }))
                            {
                                Status(session, ChangeRoommateResponseStatus.UNKNOWN);
                                return;
                            }

                            //if online, notify roommate of pending request.
                            var targetSession = Sessions.GetByAvatarId(packet.AvatarId);
                            if (targetSession != null)
                            {
                                targetSession.Write(new ChangeRoommateRequest()
                                {
                                    Type = ChangeRoommateType.INVITE,
                                    AvatarId = session.AvatarId,
                                    LotLocation = lot.location
                                });
                            }

                            Status(session, ChangeRoommateResponseStatus.INVITE_SUCCESS);
                            return;
                            //if not, we'll catch them when they log in later.
                        }
                        else if (packet.Type == ChangeRoommateType.KICK)
                        {
                            var lot = da.Lots.GetByLocation(Context.ShardId, packet.LotLocation);
                            if (lot == null) { Status(session, ChangeRoommateResponseStatus.UNKNOWN); return; }

                            var roommates = da.Roommates.GetLotRoommates(lot.lot_id);
                            if (roommates.Count(x => x.is_pending == 0) <= 1)
                            {
                                //we're the last roommate here. the lot must be closed. This will cause the lot to fall off-map.
                                if (lot.owner_id != session.AvatarId)
                                {
                                    //only the owner can delete their lot
                                    Status(session, ChangeRoommateResponseStatus.UNKNOWN); return;
                                }

                                //TODO: let user do this with their lot falling off map. for now they must use start fresh mode.
                                Status(session, ChangeRoommateResponseStatus.UNKNOWN); return;
                            }
                            else if (roommates.Any(x => x.avatar_id == packet.AvatarId && x.is_pending == 0))
                            {
                                //avatar can be removed from the lot.
                                var selfDelete = false;
                                if (session.AvatarId == packet.AvatarId)
                                {
                                    //self deletes are allowed
                                    selfDelete = true;
                                }
                                else if (lot.owner_id != session.AvatarId)
                                {
                                    //only the owner can kickout other roommates
                                    Status(session, ChangeRoommateResponseStatus.YOU_ARE_NOT_OWNER); return;
                                }

                                if (da.Roommates.RemoveRoommate(packet.AvatarId, lot.lot_id) == 0)
                                {
                                    //nothing happened when we tried to remove this user's roommate status.
                                    Status(session, ChangeRoommateResponseStatus.YOU_ARE_NOT_ROOMMATE); return;
                                }

                                if (selfDelete && lot.owner_id == session.AvatarId) {
                                    //lot needs a new owner
                                    da.Lots.ReassignOwner(lot.lot_id); //database will assign oldest roommate as owner.
                                }

                                var lotDS = await DataService.Get<FSO.Common.DataService.Model.Lot>(packet.LotLocation);
                                if (lotDS != null)
                                {
                                    lotDS.Lot_RoommateVec = lotDS.Lot_RoommateVec.Remove(packet.AvatarId);
                                    var newLot = da.Lots.Get(lot.lot_id);
                                    lotDS.Lot_OwnerVec = ImmutableList.Create(newLot.owner_id);
                                }

                                //if online, notify the lot
                                var lotOwned = da.LotClaims.GetByLotID(lot.lot_id);
                                if (lotOwned != null)
                                {
                                    var lotServer = LotServers.GetLotServerSession(lotOwned.owner);
                                    if (lotServer != null)
                                    {
                                        //immediately notify lot of new roommate
                                        lotServer.Write(new NotifyLotRoommateChange()
                                        {
                                            AvatarId = packet.AvatarId,
                                            LotId = lot.lot_id,
                                            Change = Protocol.Gluon.Model.ChangeType.REMOVE_ROOMMATE
                                        });
                                    }
                                } else
                                {
                                    //try force the lot open
                                    var result = await Lots.TryFindOrOpen(lot.location, 0, session);
                                }

                                //TODO: if offline, force the lot to open so we can remove the kicked out roommate's objects.

                                var avatar = await DataService.Get<Avatar>(packet.AvatarId);
                                if (avatar != null) avatar.Avatar_LotGridXY = 0;

                                //try to notify roommates
                                foreach (var roomie in roommates)
                                {
                                    var kickedMe = roomie.avatar_id == packet.AvatarId;
                                    if (roomie.is_pending == 0 && !(kickedMe && selfDelete) && session.AvatarId != roomie.avatar_id)
                                    {
                                        var targetSession = Sessions.GetByAvatarId(roomie.avatar_id);
                                        if (targetSession != null)
                                        {
                                            targetSession.Write(new ChangeRoommateResponse()
                                            {
                                                Type = (kickedMe)?ChangeRoommateResponseStatus.GOT_KICKED:ChangeRoommateResponseStatus.ROOMMATE_LEFT,
                                                Extra = packet.AvatarId
                                            });
                                        }
                                    }
                                }

                                if (selfDelete) { Status(session, ChangeRoommateResponseStatus.SELFKICK_SUCCESS); return; }
                                else { Status(session, ChangeRoommateResponseStatus.KICK_SUCCESS); return; }
                            }
                            else
                            {
                                //not roommate??
                                Status(session, ChangeRoommateResponseStatus.YOU_ARE_NOT_ROOMMATE); return;
                            }
                            //if target avatar is our avatar, we are moving out

                            //if we are owner of the lot, set the new owner to the first (earliest) roommate entry in the database.
                            //if we are the last person in the lot, the lot must be closed before doing this.
                            //make sure all references are set to new owner!

                            //remove roommate entry for target avatar.
                            //update lot data service and avatar data service for targets.

                            //if lot open, notify lot server of change (roommate add/remove AND new/same owner)
                            //the lot will remove objects as necessary

                            //future: if lot closed, special request to a lot server to quickly open an unjoinable instance of the lot to remove our objects.
                        }
                    }
                }
            }
            catch (Exception e) {
                Status(session, ChangeRoommateResponseStatus.UNKNOWN);
            }
        }
    }
}