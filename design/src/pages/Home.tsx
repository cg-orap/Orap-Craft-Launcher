import { useState } from 'react'
import {
  ChevronRight,
  Cloud,
  Copy,
  Cpu,
  Gamepad2,
  HardDriveDownload,
  History,
  Radio,
  Play,
  Square,
  Sun,
} from 'lucide-react'
import { backups, instances, type LaunchState, type SyncState } from '../mock'
import { Button, Card, CheckLine, cx, Dot, Progress, SectionTitle, Spinner, Tag } from '../ui'

const stages = ['校验文件', '下载资源', '启动 Java']

export default function Home({
  launch,
  setLaunch,
  sync,
  setSync,
}: {
  launch: LaunchState
  setLaunch: (s: LaunchState) => void
  sync: SyncState
  setSync: (s: SyncState) => void
}) {
  const current = instances[0]
  const [progress, setProgress] = useState(launch === 'starting' ? 42 : 0)
  const recent = instances.slice(0, 4)

  return (
    <div className="space-y-4">
      {/* 启动控制台 */}
      <Card className="overflow-hidden">
        <div className="grid grid-cols-[minmax(0,1fr)_320px] gap-0">
          <div className="p-5">
            <div className="flex items-start gap-4">
              <div
                className={cx(
                  'grid h-[92px] w-[92px] shrink-0 place-items-center rounded-md bg-gradient-to-br',
                  current.cover,
                )}
              >
                <div className="pixel-grid grid h-12 w-12 place-items-center rounded-sm bg-black/25">
                  <span className="font-pixel text-[15px] leading-none text-primary">FO</span>
                </div>
              </div>

              <div className="min-w-0">
                <div className="flex items-center gap-2">
                  <h1 className="truncate text-[19px] font-semibold tracking-tight text-tx-1">{current.name}</h1>
                  <Tag tone={current.loader}>{current.loader}</Tag>
                  <span className="font-mono text-[11px] text-tx-3">{current.loaderVersion}</span>
                </div>
                <div className="mt-1.5 flex items-center gap-3 text-[11.5px] text-tx-2">
                  <span className="font-mono">MC {current.mc}</span>
                  <span className="text-tx-3">·</span>
                  <span>{current.mods} 个模组</span>
                  <span className="text-tx-3">·</span>
                  <span>最后游玩 {current.lastPlayed}</span>
                </div>

                <div className="mt-3 flex flex-wrap gap-x-5 gap-y-1.5">
                  <CheckLine>账户 cg_orap（微软正版）</CheckLine>
                  <CheckLine>{current.java}</CheckLine>
                  <CheckLine>内存 {current.memory}</CheckLine>
                </div>
              </div>
            </div>

            {/* 主操作区：唯一的视觉焦点 */}
            <div className="mt-5">
              {launch !== 'running' ? (
                <div className="flex items-center gap-3">
                  <Button
                    variant="primary"
                    size="lg"
                    className="min-w-[196px] !py-3 text-[15px]"
                    icon={<Play className="h-4 w-4" />}
                    onClick={() => {
                      if (launch === 'starting') return
                      setLaunch('starting')
                      setProgress(12)
                    }}
                    disabled={launch === 'starting'}
                  >
                    {launch === 'starting' ? '正在启动…' : '启动游戏'}
                  </Button>
                  <Button variant="quiet" size="md" onClick={() => setLaunch('running')} icon={<Spinner className="hidden" />}>
                    模拟运行中
                  </Button>
                  <span className="text-[11px] text-tx-3">Enter 或点击启动 · 3 秒内完成选实例 → 启动</span>
                </div>
              ) : (
                <div className="flex flex-wrap items-center gap-3">
                  <div className="inline-flex min-w-[196px] items-center justify-center gap-2 rounded-md border border-primary/35 bg-primary/12 px-5 py-3 text-[15px] font-medium text-primary">
                    <Dot pulse />
                    游戏运行中
                  </div>
                  <Button variant="ghost" size="md" icon={<History className="h-3.5 w-3.5" />}>
                    查看日志
                  </Button>
                  <Button variant="danger" size="md" icon={<Square className="h-3.5 w-3.5" />} onClick={() => setLaunch('idle')}>
                    结束进程
                  </Button>
                </div>
              )}

              {launch === 'starting' ? (
                <div className="mt-4 max-w-[520px] space-y-2">
                  <div className="flex items-center justify-between text-[11px]">
                    <span className="text-tx-2">{stages[Math.min(2, Math.floor(progress / 34))]}</span>
                    <span className="font-mono text-tx-3">{progress}%</span>
                  </div>
                  <Progress value={progress} />
                  <button
                    type="button"
                    onClick={() => setProgress((p) => Math.min(100, p + 19))}
                    className="text-[11px] text-tx-3 underline-offset-2 hover:text-tx-1 hover:underline"
                  >
                    推进进度（演示）
                  </button>
                </div>
              ) : null}
            </div>
          </div>

          {/* 右侧状态栏 */}
          <div className="border-l border-border bg-surface/40 p-5">
            <SectionTitle>运行环境</SectionTitle>
            <div className="space-y-2.5">
              {[
                { icon: <Sun className="h-3.5 w-3.5" />, label: 'Java', value: current.java },
                { icon: <Cpu className="h-3.5 w-3.5" />, label: '内存分配', value: current.memory },
                { icon: <Gamepad2 className="h-3.5 w-3.5" />, label: '启动器版本', value: 'v0.2 预览' },
              ].map((row) => (
                <div key={row.label} className="flex items-center justify-between rounded-sm bg-white/[0.03] px-2.5 py-2">
                  <span className="flex items-center gap-2 text-[11.5px] text-tx-2">
                    <span className="text-tx-3">{row.icon}</span>
                    {row.label}
                  </span>
                  <span className="font-mono text-[11px] text-tx-1">{row.value}</span>
                </div>
              ))}
            </div>

            <div className="mt-4 flex items-center justify-between rounded-sm border border-border px-2.5 py-2">
              <span className="flex items-center gap-2 text-[11.5px] text-tx-2">
                <Cloud className="h-3.5 w-3.5 text-tx-3" />
                云同步
              </span>
              <button
                type="button"
                onClick={() => setSync(sync === 'synced' ? 'conflict' : 'synced')}
                className={cx(
                  'inline-flex items-center gap-1.5 rounded-sm px-2 py-0.5 text-[11px]',
                  sync === 'synced' && 'bg-success/10 text-success',
                  sync === 'syncing' && 'bg-info/10 text-info',
                  sync === 'conflict' && 'bg-warning/10 text-warning',
                )}
              >
                <Dot tone={sync === 'synced' ? 'success' : sync === 'syncing' ? 'info' : 'warning'} pulse={sync === 'syncing'} />
                {sync === 'synced' ? '已同步' : sync === 'syncing' ? '同步中' : '存在冲突'}
              </button>
            </div>
          </div>
        </div>
      </Card>

      {/* 最近实例 */}
      <div>
        <SectionTitle hint={<button className="hover:text-tx-1">查看全部</button>}>最近游玩</SectionTitle>
        <div className="grid grid-cols-4 gap-4">
          {recent.map((item) => (
            <Card key={item.id} className="group cursor-pointer p-3 transition-colors duration-200 ease-smooth hover:border-white/15 hover:bg-elevated">
              <div className={cx('mb-3 grid h-20 w-full place-items-center rounded-sm bg-gradient-to-br', item.cover)}>
                <span className="font-pixel text-[12px] text-tx-2">{item.loader.slice(0, 2).toUpperCase()}</span>
              </div>
              <div className="truncate text-[12.5px] text-tx-1">{item.name}</div>
              <div className="mt-1 flex items-center justify-between">
                <span className="font-mono text-[10.5px] text-tx-3">MC {item.mc}</span>
                <Tag tone={item.loader}>{item.loader}</Tag>
              </div>
            </Card>
          ))}
        </div>
      </div>

      {/* 备份 / 联机 / 公告 */}
      <div className="grid grid-cols-[minmax(0,1.4fr)_minmax(0,1fr)] gap-4">
        <Card className="p-4">
          <div className="flex items-center justify-between">
            <SectionTitle>自动备份</SectionTitle>
            <span className="text-[11px] text-tx-3">启动前触发 · 保留 7 份</span>
          </div>
          <div className="space-y-2">
            {backups.slice(0, 3).map((b) => (
              <div key={b.id} className="flex items-center justify-between rounded-sm border border-border/60 px-2.5 py-2">
                <div className="flex items-center gap-2.5">
                  <HardDriveDownload className="h-3.5 w-3.5 text-tx-3" />
                  <div>
                    <div className="text-[11.5px] text-tx-1">{b.instance}</div>
                    <div className="font-mono text-[10px] text-tx-3">
                      {b.time} · {b.size}
                    </div>
                  </div>
                </div>
                <span className="rounded-sm bg-white/[0.04] px-1.5 py-0.5 text-[10px] text-tx-2">{b.trigger}</span>
              </div>
            ))}
          </div>
        </Card>

        <div className="space-y-4">
          <Card className="p-4">
            <div className="flex items-center justify-between">
              <span className="flex items-center gap-2 text-[12.5px] text-tx-1">
                <Radio className="h-3.5 w-3.5 text-info" />
                P2P 联机
              </span>
              <Button variant="ghost" size="sm">创建房间</Button>
            </div>
            <p className="mt-2 text-[11px] leading-relaxed text-tx-3">
              无需公网 IP，与好友直连同乐。当前实例支持 1.20.1。
            </p>
          </Card>

          <Card className="p-4">
            <SectionTitle hint="11-02">公告</SectionTitle>
            <ul className="space-y-2">
              {[
                '新增备份时间线与快照回滚（Beta）',
                '下载源支持 Modrinth / CurseForge 切换',
                '日志页加入崩溃报告归档',
              ].map((text) => (
                <li key={text} className="flex items-start gap-2 text-[11.5px] leading-relaxed text-tx-2">
                  <ChevronRight className="mt-[3px] h-3 w-3 shrink-0 text-tx-3" />
                  {text}
                </li>
              ))}
            </ul>
          </Card>
        </div>
      </div>

      <div className="flex items-center gap-4 pb-2 pt-1 text-[10.5px] text-tx-3">
        <span className="inline-flex items-center gap-1.5">
          <Copy className="h-3 w-3" /> 数据目录 D:\OrapCraft
        </span>
        <span>UI 概念稿 · 数据全部为 mock</span>
      </div>
    </div>
  )
}