import { useState } from 'react'
import { ChevronRight, Cloud, Cpu, Play, Radio, RotateCcw, Square, Sun, Terminal } from 'lucide-react'
import { backups, instances, type LaunchState, type SyncState } from '../mock'
import { Button, Card, Cover, cx, Dot, Progress, Spinner, Tag } from '../ui'

const stages = ['校验文件', '下载资源', '启动 Java']

export default function Home({
  launch,
  setLaunch,
  sync,
  setSync,
  onOpenLogs,
}: {
  launch: LaunchState
  setLaunch: (s: LaunchState) => void
  sync: SyncState
  setSync: (s: SyncState) => void
  onOpenLogs: () => void
}) {
  const current = instances[0]
  const [progress, setProgress] = useState(launch === 'starting' ? 42 : 0)

  return (
    <div className="space-y-5 pb-16">
      {/* ── 启动控制台 ─────────────────────────────── */}
      <section className="rounded-lg border border-border bg-surface p-6">
        <div className="flex items-start gap-4">
          <Cover seed={current.id} label={current.loader.slice(0, 2).toUpperCase()} className="h-[72px] w-[72px] shrink-0" tall />
          <div className="min-w-0 flex-1">
            <div className="flex items-center gap-2 text-[11px] text-tx-3">
              <span>当前实例</span>
              <ChevronRight className="h-3 w-3" />
              <span className="font-mono text-tx-2">MC {current.mc}</span>
            </div>

            <h1 className="mt-1 truncate text-[22px] font-semibold tracking-tight text-tx-1">{current.name}</h1>

            <div className="mt-2 flex flex-wrap items-center gap-x-3 gap-y-1.5 text-[11.5px] text-tx-2">
              <Tag tone={current.loader}>{current.loader}</Tag>
              <span className="font-mono text-tx-3">{current.loaderVersion}</span>
              <span className="text-tx-3">·</span>
              <span>{current.mods} 个模组</span>
              <span className="text-tx-3">·</span>
              <span>最后游玩 {current.lastPlayed}</span>
            </div>
          </div>

        </div>

        {/* 主操作 */}
        <div className="mt-6 flex flex-wrap items-center gap-x-4 gap-y-3">
          {launch !== 'running' ? (
            <Button
              variant="primary"
              className="min-w-[188px] !py-3 text-[15px]"
              icon={launch === 'starting' ? <Spinner /> : <Play className="h-4 w-4" />}
              onClick={() => {
                if (launch === 'starting') return
                setLaunch('starting')
                setProgress(12)
              }}
              disabled={launch === 'starting'}
            >
              {launch === 'starting' ? '正在启动…' : '启动游戏'}
            </Button>
          ) : (
            <>
              <div className="inline-flex min-w-[188px] items-center justify-center gap-2 rounded-md border border-primary/35 bg-primary/12 px-5 py-3 text-[15px] font-medium text-primary">
                <Dot pulse />
                游戏运行中
              </div>
              <Button variant="ghost" size="md" icon={<Terminal className="h-3.5 w-3.5" />} onClick={onOpenLogs}>
                查看日志
              </Button>
              <Button variant="danger" size="md" icon={<Square className="h-3.5 w-3.5" />} onClick={() => setLaunch('idle')}>
                结束进程
              </Button>
            </>
          )}

          {/* 状态胶囊：把环境信息压成一行，替代原来的右侧面板 */}
          <div className="flex flex-wrap items-center gap-x-4 gap-y-1.5 text-[11.5px] text-tx-2">
            <span className="inline-flex items-center gap-1.5"><Sun className="h-3.5 w-3.5 text-tx-3" />{current.java}</span>
            <span className="inline-flex items-center gap-1.5"><Cpu className="h-3.5 w-3.5 text-tx-3" />{current.memory}</span>
            <button
              type="button"
              onClick={() => setSync(sync === 'synced' ? 'conflict' : 'synced')}
              className={cx(
                'inline-flex items-center gap-1.5 rounded-sm px-1.5 py-0.5 transition-colors duration-150',
                sync === 'synced' && 'text-success hover:bg-success/10',
                sync === 'syncing' && 'text-info hover:bg-info/10',
                sync === 'conflict' && 'text-warning hover:bg-warning/10',
              )}
            >
              <Cloud className="h-3.5 w-3.5" />
              <Dot tone={sync === 'synced' ? 'success' : sync === 'syncing' ? 'info' : 'warning'} pulse={sync === 'syncing'} />
              {sync === 'synced' ? '已同步' : sync === 'syncing' ? '同步中' : '待解决冲突'}
            </button>
          </div>
        </div>

        {/* 启动进度 */}
        {launch === 'starting' ? (
          <div className="mt-5 max-w-[480px] space-y-2">
            <div className="flex items-center justify-between text-[11px]">
              <span className="text-tx-2">{stages[Math.min(2, Math.floor(progress / 34))]}</span>
              <span className="font-mono text-tx-3">{progress}%</span>
            </div>
            <Progress value={progress} />
          </div>
        ) : null}
      </section>

      {/* ── 最近游玩 ───────────────────────────────── */}
      <section>
        <div className="mb-3 flex items-center justify-between">
          <h2 className="text-[12px] font-medium tracking-wide text-tx-2">最近游玩</h2>
          <button type="button" className="inline-flex items-center gap-0.5 text-[11px] text-tx-3 hover:text-tx-1">
            全部实例 <ChevronRight className="h-3 w-3" />
          </button>
        </div>
        <div className="grid grid-cols-4 gap-3">
          {instances.map((item, index) => (
            <button
              key={item.id}
              type="button"
              className={cx(
                'group rounded-md border p-2.5 text-left transition-all duration-200 ease-smooth',
                index === 0
                  ? 'border-white/15 bg-elevated'
                  : 'border-border bg-surface hover:border-white/15 hover:bg-elevated',
              )}
            >
              <Cover seed={item.id} label={item.loader.slice(0, 2).toUpperCase()} />
              <div className="mt-2.5 truncate text-[12px] text-tx-1">{item.name}</div>
              <div className="mt-1 flex items-center justify-between">
                <span className="font-mono text-[10px] text-tx-3">MC {item.mc}</span>
                <span className="text-[10px] text-tx-3">{item.lastPlayed}</span>
              </div>
            </button>
          ))}
        </div>
      </section>

      {/* ── 一行状态条：备份 / P2P / 公告 ────────────── */}
      <section className="grid grid-cols-3 gap-3">
        <Card className="flex items-center justify-between px-4 py-3">
          <div className="min-w-0">
            <div className="text-[11px] text-tx-3">最近备份</div>
            <div className="mt-0.5 truncate text-[12px] text-tx-1">
              {backups[0].time} · {backups[0].size}
            </div>
          </div>
          <Button variant="quiet" size="sm" icon={<RotateCcw className="h-3 w-3" />}>恢复</Button>
        </Card>

        <Card className="flex items-center justify-between px-4 py-3">
          <div className="min-w-0">
            <div className="flex items-center gap-1.5 text-[11px] text-tx-3">
              <Radio className="h-3 w-3 text-info" /> P2P 联机
            </div>
            <div className="mt-0.5 truncate text-[12px] text-tx-1">支持 {current.mc} · 无需公网 IP</div>
          </div>
          <Button variant="quiet" size="sm">创建房间</Button>
        </Card>

        <Card className="flex items-center justify-between px-4 py-3">
          <div className="min-w-0">
            <div className="text-[11px] text-tx-3">公告 · 11-02</div>
            <div className="mt-0.5 truncate text-[12px] text-tx-1">备份时间线与快照回滚已上线</div>
          </div>
          <ChevronRight className="h-3.5 w-3.5 shrink-0 text-tx-3" />
        </Card>
      </section>
    </div>
  )
}