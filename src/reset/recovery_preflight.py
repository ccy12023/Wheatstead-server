"""Build and bind offline recovery preflight input.

The caller must keep the world stopped and the stage private while this runs.
The engine runs without privileges, network access, or access to world files.
A matching digest alone is not authorization to install a recovery.
"""
import dataclasses, hashlib, json, sqlite3, subprocess, os, stat, re
from contextlib import closing
from pathlib import Path
import restore_stage, worker

APPROVED = Path('/etc/wheatstead-official-install/approved.json')
RELEASES = Path('/opt/wheatstead-world/releases')
BWRAP = Path('/usr/bin/bwrap')


def _pairs(pairs):
    out = {}
    for key, value in pairs:
        if key in out: raise RuntimeError('duplicate JSON property')
        out[key] = value
    return out

def build(stage, cfg, generation, save_path):
    stage=Path(stage)
    database=stage/'control.sqlite'
    if any(database.with_name(database.name+suffix).exists() or database.with_name(database.name+suffix).is_symlink()
           for suffix in ('-wal','-shm','-journal')): raise RuntimeError('stage database has sidecars')
    before=restore_stage._manifest(stage)
    world=stage/'world'
    owner_info=world.stat(); owner=(owner_info.st_uid,owner_info.st_gid)
    stage_cfg=dataclasses.replace(cfg,world_root=world)
    with closing(sqlite3.connect('file:'+str(stage/'control.sqlite')+'?mode=ro&immutable=1',uri=True)) as db:
        if db.execute('PRAGMA quick_check').fetchone()!=('ok',): raise RuntimeError('database invalid')
        if db.execute('SELECT generation FROM worlds WHERE id=?',(cfg.world_id,)).fetchone()!=(generation,): raise RuntimeError('generation mismatch')
        rows=db.execute('SELECT generation,map_json FROM official_world_maps WHERE world_id=?',(cfg.world_id,)).fetchall()
        if len(rows)!=1 or rows[0][0]!=generation: raise RuntimeError('map generation mismatch')
        registered=json.loads(rows[0][1],object_pairs_hook=_pairs)
        if list(worker._save_stamps(world)) != [save_path] or not worker._validated_checkpoint(stage_cfg,generation,owner,save_path) or not worker._validated_registered_map(stage_cfg,db,generation,save_path,owner): raise RuntimeError('checkpoint invalid')
        snapshot=json.loads((world/save_path).read_bytes(),object_pairs_hook=_pairs)
        restore_stage._check_world(db,cfg.world_id,generation,snapshot,registered)
        owners=[{'plot':p,'steam':sid} for p,sid in db.execute('SELECT plot_id,steam_id FROM plot_owners WHERE world_id=? ORDER BY plot_id',(cfg.world_id,))]
        owner_by_plot={x['plot']:x['steam'] for x in owners}
        houses=[{'plot':p,'owner':owner_by_plot[p],'house':h,'state':s,'blueprint':b,'x':x,'y':y} for p,h,s,b,x,y in db.execute('SELECT plot_id,house_id,state,blueprint_id,house_x,house_y FROM plot_houses WHERE world_id=? ORDER BY plot_id',(cfg.world_id,))]
        entities=[]
        for scene, obj in sorted(snapshot['world'].items()):
            for e in obj['entities']:
                entry={'id':e['id'],'scene':scene,'type':e.get('type'),'layer':e.get('layer',0)}
                for key in ('cell_x','cell_y','building','blueprint','x','y'):
                    if key in e:entry[key]=e[key]
                if 'official_origin_house_id' in e:entry['origin']=e['official_origin_house_id']
                entry['turns']=e.get('quarter_turns',0)
                entities.append(entry)
        entities.sort(key=lambda e:(e['scene'],e['id'],json.dumps(e,sort_keys=True,ensure_ascii=False,separators=(',',':'))))
        result={'version':1,'world_id':cfg.world_id,'generation':generation,'plots':sorted(registered['plots'],key=lambda p:p['id']),'owners':owners,'houses':houses,'entities':entities}
    payload=json.dumps(result,sort_keys=True,ensure_ascii=False,separators=(',',':'),allow_nan=False).encode()
    if len(payload)>64*1024*1024: raise RuntimeError('input too large')
    if restore_stage._manifest(stage)!=before: raise RuntimeError('stage changed during construction')
    return payload,before

def verify(stage,cfg,generation,save_path,runner):
    payload,manifest=build(stage,cfg,generation,save_path)
    rc,output=runner(payload)
    marker='PREFLIGHT-PASS '+hashlib.sha256(payload).hexdigest()
    lines=[line for line in output.splitlines() if line.startswith('PREFLIGHT-')]
    if rc != 0 or lines!=[marker]:raise RuntimeError('preflight failed or unbound response')
    if restore_stage._manifest(stage)!=manifest:raise RuntimeError('stage changed during preflight')
    return manifest



def verify_private_stage(stage):
    stage=Path(stage)
    for path in (stage, stage.parent):
        info=path.lstat()
        if not stat.S_ISDIR(info.st_mode) or info.st_uid != 0 or stat.S_IMODE(info.st_mode) & 0o077:
            raise RuntimeError('stage is not root exclusive')
    path=stage.parent.parent
    while True:
        info=path.lstat()
        if not stat.S_ISDIR(info.st_mode) or info.st_uid != 0 or stat.S_IMODE(info.st_mode) & 0o022:
            raise RuntimeError('untrusted stage ancestor')
        if path == path.parent:break
        path=path.parent

def release_manifest(release):
    release=Path(release)
    path=release
    while True:
        info=path.lstat()
        if not stat.S_ISDIR(info.st_mode) or info.st_uid != 0 or stat.S_IMODE(info.st_mode) & 0o022:
            raise RuntimeError('untrusted release directory')
        if path==path.parent:break
        path=path.parent
    result=restore_stage._manifest(release)
    for item in result.values():
        if item[2]!=0 or item[1]&0o022:raise RuntimeError('untrusted release file permissions')
    return result


def approved_release():
    # Only the root-reviewed release may participate in production recovery.
    for path in (APPROVED.parent, *APPROVED.parent.parents):
        info=path.lstat()
        if not stat.S_ISDIR(info.st_mode) or info.st_uid != 0 or info.st_mode & 0o022:
            raise RuntimeError('untrusted approval directory')
    info=APPROVED.lstat()
    if not stat.S_ISREG(info.st_mode) or info.st_uid != 0 or info.st_mode & 0o077:
        raise RuntimeError('untrusted release approval')
    approved=json.loads(APPROVED.read_bytes(),object_pairs_hook=_pairs)
    release=Path(approved['world_release'])
    name=approved['world_executable']
    if (release.parent != RELEASES or not re.fullmatch(r'[A-Za-z0-9_-]+',release.name)
        or not isinstance(name,str) or not re.fullmatch(r'[A-Za-z0-9_-]+',name)):
        raise RuntimeError('untrusted release path')
    manifest=release_manifest(release)
    actual={path:item[-1] for path,item in manifest.items() if item[0]=='file'}
    if (not isinstance(approved.get('world_files'),dict) or actual != approved['world_files']
        or name not in actual or name+'.pck' not in actual
        or not (release/name).stat().st_mode & 0o111):
        raise RuntimeError('release does not match approval')
    return release,name,manifest


def run_isolated(release, executable, uid, gid, payload):
    if uid <= 0 or gid <= 0: raise RuntimeError('preflight user must not be root')
    info=BWRAP.lstat()
    if not stat.S_ISREG(info.st_mode) or info.st_uid != 0 or info.st_mode & 0o022:
        raise RuntimeError('untrusted sandbox executable')
    args=[str(BWRAP),'--unshare-all','--die-with-parent','--new-session',
          '--ro-bind','/usr','/usr','--symlink','usr/lib','/lib',
          '--symlink','usr/lib64','/lib64','--proc','/proc','--dev','/dev',
          '--tmpfs','/tmp','--ro-bind',str(release),'/release','--chdir','/release',
          '--clearenv','--setenv','HOME','/tmp','--setenv','XDG_DATA_HOME','/tmp/data',
          '--setenv','XDG_CONFIG_HOME','/tmp/config','--setenv','XDG_CACHE_HOME','/tmp/cache',
          '--','/release/'+executable,'--headless','--','--recovery-preflight']
    result=subprocess.run(args,input=payload,stdout=subprocess.PIPE,stderr=subprocess.DEVNULL,
                          timeout=120,check=False,user=uid,group=gid,extra_groups=())
    return result.returncode,result.stdout.decode('utf-8',errors='replace')


def verify_production(stage,cfg,generation,save_path,owner):
    verify_private_stage(stage)
    release,executable,before=approved_release()
    result=verify(stage,cfg,generation,save_path,
                  lambda payload:run_isolated(release,executable,*owner,payload))
    if approved_release() != (release,executable,before):
        raise RuntimeError('release changed during preflight')
    return result,(release,executable,before)
