import { useEffect, useState } from 'react'
import { ChevronRight, Cloud, Cpu, Gauge, HardDrive, MemoryStick, Play, RotateCcw, Square, Sun, Terminal } from 'lucide-react'
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
    <div className="space-y-5 pb-24">
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

      {/* ── 运行性能 ───────────────────────────────── */}
      <PerfPanel launch={launch} memoryMb={Number(current.memory.replace(/[^0-9]/g, ''))} />

      {/* ── 最近游玩（纵向长条列表） ─────────────────── */}
      <section>
        <div className="mb-3 flex items-center justify-between">
          <h2 className="text-[12px] font-medium tracking-wide text-tx-2">最近游玩</h2>
          <button type="button" className="inline-flex items-center gap-0.5 text-[11px] text-tx-3 hover:text-tx-1">
            全部实例 <ChevronRight className="h-3 w-3" />
          </button>
        </div>

        <div className="space-y-1.5">
          {instances.map((item, index) => (
            <button
              key={item.id}
              type="button"
              className={cx(
                'group flex w-full items-center gap-3 rounded-md border px-3 py-2 text-left transition-all duration-200 ease-smooth',
                index === 0
                  ? 'border-bstrong bg-elevated'
                  : 'border-border bg-surface hover:border-bstrong hover:bg-elevated',
              )}
            >
              <Cover
                seed={item.id}
                label={item.loader.slice(0, 2).toUpperCase()}
                className="h-9 w-9 shrink-0"
              />

              <span className="min-w-0 flex-1">
                <span className="flex items-center gap-2">
                  <span className="truncate text-[12.5px] text-tx-1">{item.name}</span>
                  {index === 0 ? (
                    <span className="rounded-sm bg-primary/12 px-1.5 py-0.5 text-[10px] text-primary">当前</span>
                  ) : null}
                </span>
                <span className="mt-0.5 flex items-center gap-2 font-mono text-[10.5px] text-tx-3">
                  <span>MC {item.mc}</span>
                  <span className="text-tx-3/60">·</span>
                  <span>{item.loader}</span>
                  <span className="text-tx-3/60">·</span>
                  <span>{item.mods} 模组</span>
                </span>
              </span>

              <span className="hidden shrink-0 items-center gap-3 text-[10.5px] text-tx-3 sm:flex">
                <span>{item.lastPlayed}</span>
                <Tag tone={item.loader}>{item.loader}</Tag>
              </span>

              <span className="grid h-7 w-7 shrink-0 place-items-center rounded-sm text-tx-3 transition-colors duration-150 group-hover:bg-tint2 group-hover:text-tx-1">
                <Play className="h-3.5 w-3.5" />
              </span>
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
              <HardDrive className="h-3 w-3 text-info" /> 存储占用
            </div>
            <div className="mt-0.5 truncate text-[12px] text-tx-1">实例 2.6 GB · 缓存 812 MB</div>
          </div>
          <Button variant="quiet" size="sm">清理缓存</Button>
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
/** 运行性能面板：内存 / CPU / GPU / 运行状态。数值为演示用 mock，运行中会自动波动。 */
function PerfPanel({ launch, memoryMb }: { launch: LaunchState; memoryMb: number }) {
  const running = launch === 'running'
  const starting = launch === 'starting'

  const [sample, setSample] = useState({ ram: 1180, cpu: 4, gpu: 2, seconds: 0 })

  useEffect(() => {
    const timer = setInterval(() => {
      setSample((prev) => {
        if (running) {
          return {
            ram: Math.round(memoryMb * (0.52 + Math.random() * 0.16)),
            cpu: Math.round(38 + Math.random() * 26),
            gpu: Math.round(46 + Math.random() * 28),
            seconds: prev.seconds + 2,
          }
        }
        if (starting) {
          return {
            ram: Math.round(prev.ram * 0.6 + memoryMb * 0.2),
            cpu: Math.round(16 + Math.random() * 14),
            gpu: Math.round(6 + Math.random() * 8),
            seconds: 0,
          }
        }
        return {
          ram: Math.round(980 + Math.random() * 240),
          cpu: Math.round(1 + Math.random() * 5),
          gpu: Math.round(0 + Math.random() * 3),
          seconds: 0,
        }
      })
    }, 1600)
    return () => clearInterval(timer)
  }, [running, starting, memoryMb])

  const ramPercent = Math.min(100, Math.round((sample.ram / Math.max(1, memoryMb)) * 100))
  const state = running ? '运行中' : starting ? '启动中' : '就绪'
  const stateTone = running ? 'text-primary' : starting ? 'text-info' : 'text-tx-2'

  return (
    <section className="rounded-lg border border-border bg-surface px-5 py-4">
      <div className="mb-3 flex items-center justify-between">
        <h2 className="flex items-center gap-1.5 text-[12px] font-medium tracking-wide text-tx-2">
          <Gauge className="h-3.5 w-3.5 text-tx-3" />
          性能
        </h2>
        <span className="font-mono text-[10px] text-tx-3">
          {running ? `已运行 ${Math.floor(sample.seconds / 60)} 分 ${sample.seconds % 60} 秒` : '采样间隔 1.6s · 演示数据'}
        </span>
      </div>

      <div className="grid grid-cols-4 gap-5">
        {/* 内存 */}
        <div>
          <div className="flex items-center justify-between text-[11px] text-tx-3">
            <span className="inline-flex items-center gap-1.5"><MemoryStick className="h-3 w-3" />内存</span>
            <span className="font-mono text-tx-2">{sample.ram} / {memoryMb} MB</span>
          </div>
          <div className="mt-2 h-1.5 w-full overflow-hidden rounded-full bg-tint2">
            <div
              className={cx('h-full rounded-full transition-[width] duration-700 ease-smooth', ramPercent > 85 ? 'bg-warning' : 'bg-primary')}
              style={{ width: ramPercent + '%' }}
            />
          </div>
        </div>

        {/* CPU */}
        <div>
          <div className="flex items-center justify-between text-[11px] text-tx-3">
            <span className="inline-flex items-center gap-1.5"><Cpu className="h-3 w-3" />CPU</span>
            <span className="font-mono text-tx-2">{sample.cpu}%</span>
          </div>
          <div className="mt-2 h-1.5 w-full overflow-hidden rounded-full bg-tint2">
            <div className="h-full rounded-full bg-info transition-[width] duration-700 ease-smooth" style={{ width: sample.cpu + '%' }} />
          </div>
        </div>

        {/* GPU */}
        <div>
          <div className="flex items-center justify-between text-[11px] text-tx-3">
            <span className="inline-flex items-center gap-1.5"><Gauge className="h-3 w-3" />GPU</span>
            <span className="font-mono text-tx-2">{sample.gpu}%</span>
          </div>
          <div className="mt-2 h-1.5 w-full overflow-hidden rounded-full bg-tint2">
            <div className="h-full rounded-full bg-accent/80 transition-[width] duration-700 ease-smooth" style={{ width: sample.gpu + '%' }} />
          </div>
        </div>

        {/* 运行状态 */}
        <div>
          <div className="text-[11px] text-tx-3">运行状态</div>
          <div className="mt-2 flex items-center gap-1.5">
            <Dot tone={running ? 'success' : starting ? 'info' : 'success'} pulse={running || starting} />
            <span className={cx('text-[12px]', stateTone)}>{state}</span>
            <span className="ml-auto font-mono text-[10.5px] text-tx-3">Java 17</span>
          </div>
        </div>
      </div>
    </section>
  )
}
