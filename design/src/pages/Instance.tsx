import { useState } from 'react'
import { Camera, Clock, Download, FileDiff, GitCompare, RotateCcw, Settings2, Trash2 } from 'lucide-react'
import { backups, instances } from '../mock'
import { Button, Card, CheckLine, cx, PixelArt, Progress, SectionTitle, StatRow, Tag } from '../ui'

const tabs = ['概览', '模组', '存档', '日志', '快照', '设置'] as const

const snapshots = [
  { id: 's1', time: '今天 22:30', label: '启动前快照', size: '412 MB', delta: '+3 模组 / -1 模组' },
  { id: 's2', time: '昨天 21:40', label: '手动创建', size: '408 MB', delta: '无变化' },
  { id: 's3', time: '10-24 19:12', label: '启动前快照', size: '397 MB', delta: '+12 配置变更' },
  { id: 's4', time: '10-22 08:05', label: '更新前快照', size: '381 MB', delta: '模组目录重建' },
]

export default function Instance() {
  const inst = instances[0]
  const [tab, setTab] = useState<(typeof tabs)[number]>('概览')
  const [rollbackTarget, setRollbackTarget] = useState<string | null>(null)

  return (
    <div className="space-y-4">
      <Card className="p-5">
        <div className="flex items-start gap-4">
          <div className={cx('grid h-[84px] w-[84px] place-items-center rounded-md bg-gradient-to-br', inst.cover)}>
            <span className="font-pixel text-[13px] text-primary">FO</span>
          </div>
          <div className="min-w-0 flex-1">
            <div className="flex items-center gap-2">
              <h1 className="truncate text-[18px] font-semibold text-tx-1">{inst.name}</h1>
              <Tag tone={inst.loader}>{inst.loader}</Tag>
              <span className="font-mono text-[11px] text-tx-3">{inst.loaderVersion}</span>
            </div>
            <div className="mt-1.5 flex flex-wrap items-center gap-x-3 gap-y-1 text-[11.5px] text-tx-2">
              <span className="font-mono">MC {inst.mc}</span>
              <span className="text-tx-3">·</span>
              <span>{inst.mods} 个模组</span>
              <span className="text-tx-3">·</span>
              <span>最后游玩 {inst.lastPlayed}</span>
              <span className="text-tx-3">·</span>
              <span className="font-mono">2.6 GB</span>
            </div>
          </div>
          <div className="flex items-center gap-2">
            <Button variant="ghost" size="sm" icon={<Settings2 className="h-3.5 w-3.5" />}>实例设置</Button>
            <Button variant="danger" size="sm" icon={<Trash2 className="h-3.5 w-3.5" />}>删除实例</Button>
          </div>
        </div>

        <div className="mt-4 flex items-center gap-1 border-t border-border pt-3">
          {tabs.map((item) => (
            <button
              key={item}
              type="button"
              onClick={() => setTab(item)}
              className={cx(
                'rounded-sm px-2.5 py-1.5 text-[12px] transition-colors duration-150 ease-smooth',
                tab === item ? 'bg-tint2 text-tx-1' : 'text-tx-3 hover:bg-tint hover:text-tx-2',
              )}
            >
              {item}
            </button>
          ))}
        </div>
      </Card>

      {tab === '概览' ? (
        <div className="grid grid-cols-[minmax(0,1.35fr)_minmax(0,1fr)] gap-4">
          <Card className="p-4">
            <SectionTitle hint="今日 22:35">备份时间线</SectionTitle>
            <ol className="relative ml-1 border-l border-border pl-4">
              {snapshots.map((s, i) => (
                <li key={s.id} className="relative pb-4 last:pb-0">
                  <span
                    className={cx(
                      'absolute -left-[21px] top-1 grid h-3 w-3 place-items-center rounded-full border',
                      i === 0 ? 'border-primary bg-primary/25' : 'border-border bg-surface',
                    )}
                  >
                    <span className={cx('h-1 w-1 rounded-full', i === 0 ? 'bg-primary' : 'bg-tx-3')} />
                  </span>
                  <div className="flex items-start justify-between gap-3">
                    <div>
                      <div className="flex items-center gap-2">
                        <span className="text-[12px] text-tx-1">{s.label}</span>
                        <span className="font-mono text-[10.5px] text-tx-3">{s.time}</span>
                      </div>
                      <div className="mt-0.5 text-[10.5px] text-tx-3">{s.delta}</div>
                    </div>
                    <div className="flex items-center gap-1.5">
                      <span className="font-mono text-[10.5px] text-tx-3">{s.size}</span>
                      <Button variant="quiet" size="sm" icon={<GitCompare className="h-3 w-3" />}>对比</Button>
                      <Button
                        variant="danger"
                        size="sm"
                        icon={<RotateCcw className="h-3 w-3" />}
                        onClick={() => setRollbackTarget(s.time)}
                      >
                        回滚
                      </Button>
                    </div>
                  </div>
                </li>
              ))}
            </ol>

            <div className="mt-2 flex items-center gap-2 border-t border-border pt-3">
              <Button variant="ghost" size="sm" icon={<Camera className="h-3.5 w-3.5" />}>创建快照</Button>
              <Button variant="quiet" size="sm" icon={<FileDiff className="h-3.5 w-3.5" />}>与上一份对比</Button>
              <span className="ml-auto text-[10.5px] text-tx-3">快照存放在 instances/&lt;id&gt;/snapshots</span>
            </div>
          </Card>

          <div className="space-y-4">
            <Card className="p-4">
              <SectionTitle>概览</SectionTitle>
              <StatRow label="游戏版本" value={inst.mc} />
              <StatRow label="加载器" value={inst.loader + ' ' + inst.loaderVersion} />
              <StatRow label="模组数量" value={inst.mods} />
              <StatRow label="Java" value={inst.java} />
              <StatRow label="内存分配" value={inst.memory} />
              <StatRow label="最近备份" value={backups[0].time} />
            </Card>

            <Card className="p-4">
              <SectionTitle hint="只读统计">目录占用</SectionTitle>
              <div className="space-y-2.5">
                {[
                  { label: 'mods', value: 62, size: '1.4 GB' },
                  { label: 'saves', value: 21, size: '233 MB' },
                  { label: 'resourcepacks', value: 12, size: '418 MB' },
                  { label: 'logs', value: 6, size: '48 MB' },
                ].map((row) => (
                  <div key={row.label}>
                    <div className="mb-1 flex items-center justify-between font-mono text-[10.5px] text-tx-3">
                      <span>{row.label}</span>
                      <span>{row.size}</span>
                    </div>
                    <Progress value={row.value} tone="info" />
                  </div>
                ))}
              </div>
            </Card>
          </div>
        </div>
      ) : null}

      {tab === '模组' ? (
        <Card className="p-4">
          <div className="mb-3 flex items-center justify-between">
            <div className="flex items-center gap-2 text-[12px] text-tx-2">
              <span className="font-mono text-tx-1">{inst.mods}</span> 个模组
              <span className="text-tx-3">·</span>
              <span>2 个待更新</span>
            </div>
            <div className="flex items-center gap-2">
              <Button variant="ghost" size="sm" icon={<Download className="h-3.5 w-3.5" />}>从市场安装</Button>
              <Button variant="primary" size="sm">检查更新</Button>
            </div>
          </div>
          <div className="divide-y divide-border">
            {[
              ['fabric-api', '0.92.5+1.20.1', '正常'],
              ['bettercombat', '1.8.6+1.20.1', '待更新 → 1.9.0'],
              ['alexscaves', '1.1.2', '正常'],
              ['modernfix', '5.19.4', '正常'],
              ['sodium', '0.5.11', '待更新 → 0.6.0'],
            ].map(([name, ver, state]) => (
              <div key={name} className="flex items-center justify-between py-2.5">
                <div className="flex items-center gap-3">
                  <span className="grid h-7 w-7 place-items-center rounded-sm bg-tint font-pixel text-[9px] text-tx-2">
                    {name.slice(0, 2).toUpperCase()}
                  </span>
                  <div>
                    <div className="text-[12px] text-tx-1">{name}</div>
                    <div className="font-mono text-[10px] text-tx-3">{ver}</div>
                  </div>
                </div>
                <span
                  className={cx(
                    'text-[10.5px]',
                    state === '正常' ? 'text-tx-3' : 'text-warning',
                  )}
                >
                  {state}
                </span>
              </div>
            ))}
          </div>
        </Card>
      ) : null}

      {tab !== '概览' && tab !== '模组' ? (
        <Card className="flex flex-col items-center justify-center gap-3 py-14">
          <PixelArt variant="empty" />
          <div className="text-[12px] text-tx-2">「{tab}」页在本期概念稿中为占位</div>
          <div className="text-[11px] text-tx-3">已实现：概览（含快照时间线）与模组列表</div>
        </Card>
      ) : null}

      <div className="flex items-center gap-2 text-[10.5px] text-tx-3">
        <Clock className="h-3 w-3" />
        实例隔离：mods / saves / config 均在本实例目录内，与其它版本互不影响
      </div>

      {rollbackTarget ? (
        <div className="fixed inset-0 z-30 grid place-items-center bg-scrim backdrop-blur-[2px]">
          <Card elevated className="w-[380px] p-5">
            <div className="text-[13.5px] font-medium text-tx-1">回滚到这个快照？</div>
            <p className="mt-2 text-[11.5px] leading-relaxed text-tx-2">
              将把当前实例恢复到「{rollbackTarget}」的状态。回滚前会自动把当前状态另存为新快照，可再次回滚。
            </p>
            <div className="mt-4 flex justify-end gap-2">
              <Button variant="quiet" size="sm" onClick={() => setRollbackTarget(null)}>取消</Button>
              <Button variant="danger" size="sm" onClick={() => setRollbackTarget(null)}>确认回滚</Button>
            </div>
          </Card>
        </div>
      ) : null}
    </div>
  )
}