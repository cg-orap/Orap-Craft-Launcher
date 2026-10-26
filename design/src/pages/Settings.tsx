import { useState } from 'react'
import { Check, ChevronRight, Download, FolderTree, HardDrive, Palette, Cpu, Wrench } from 'lucide-react'
import { themes, type ThemeId } from '../themes'
import { Button, Card, cx, Dot } from '../ui'

const sections = [
  { id: 'look', name: '外观', icon: <Palette className="h-3.5 w-3.5" /> },
  { id: 'java', name: 'Java', icon: <Cpu className="h-3.5 w-3.5" /> },
  { id: 'source', name: '下载源', icon: <Download className="h-3.5 w-3.5" /> },
  { id: 'backup', name: '备份', icon: <FolderTree className="h-3.5 w-3.5" /> },
  { id: 'advanced', name: '高级', icon: <Wrench className="h-3.5 w-3.5" /> },
] as const

type SectionId = (typeof sections)[number]['id']

export default function Settings({
  theme,
  onTheme,
}: {
  theme: ThemeId
  onTheme: (id: ThemeId) => void
}) {
  const [section, setSection] = useState<SectionId>('look')
  const [animations, setAnimations] = useState(true)
  const [titleGlass, setTitleGlass] = useState(true)
  const [source, setSource] = useState<'official' | 'bmclapi'>('bmclapi')

  return (
    <div className="flex h-[calc(100vh-40px-48px)] min-h-[520px] flex-col gap-4">
      <div>
        <h1 className="text-[17px] font-semibold tracking-tight text-tx-1">设置</h1>
        <p className="mt-1 text-[11.5px] text-tx-2">分组：外观、Java、下载源、备份、高级。联机功能暂未开放。</p>
      </div>

      <div className="grid min-h-0 flex-1 grid-cols-[188px_minmax(0,1fr)] gap-4">
        <Card className="h-fit p-2">
          {sections.map((s) => (
            <button
              key={s.id}
              type="button"
              onClick={() => setSection(s.id)}
              className={cx(
                'flex w-full items-center gap-2 rounded-sm px-2.5 py-2 text-[12px] transition-colors duration-150',
                section === s.id ? 'bg-white/[0.07] text-tx-1' : 'text-tx-3 hover:bg-white/[0.04] hover:text-tx-2',
              )}
            >
              {s.icon}
              {s.name}
              {section === s.id ? <ChevronRight className="ml-auto h-3 w-3" /> : null}
            </button>
          ))}
        </Card>

        <Card className="min-h-0 overflow-y-auto p-5">
          {section === 'look' ? (
            <div className="space-y-6">
              <div>
                <div className="text-[12.5px] text-tx-1">主题</div>
                <div className="mt-1 text-[11px] text-tx-3">三套预设，切换立即生效，不做自定义调色器。</div>
                <div className="mt-3 grid grid-cols-3 gap-3">
                  {themes.map((t) => (
                    <button
                      key={t.id}
                      type="button"
                      onClick={() => onTheme(t.id)}
                      className={cx(
                        'rounded-md border p-3 text-left transition-all duration-200 ease-smooth',
                        theme === t.id ? 'border-primary/50 bg-primary/[0.06]' : 'border-border hover:border-white/15 hover:bg-white/[0.03]',
                      )}
                    >
                      <div className="flex items-center justify-between">
                        <span className="text-[12px] text-tx-1">{t.name}</span>
                        {theme === t.id ? <Check className="h-3.5 w-3.5 text-primary" /> : null}
                      </div>
                      <div className="mt-2 flex gap-1">
                        {t.swatch.map((c) => (
                          <span key={c} className="h-4 flex-1 rounded-[2px] border border-white/10" style={{ background: c }} />
                        ))}
                      </div>
                      <div className="mt-2 text-[10.5px] leading-relaxed text-tx-3">{t.desc}</div>
                    </button>
                  ))}
                </div>
              </div>

              <div className="space-y-2.5">
                <Row title="背景图" desc="主页使用自定义图片，叠加 40% 暗色蒙版保证对比度">
                  <Button variant="ghost" size="sm">选择图片</Button>
                </Row>
                <Row title="标题栏材质" desc="仅标题栏、浮层与右键菜单使用毛玻璃">
                  <Switch on={titleGlass} onChange={setTitleGlass} />
                </Row>
                <Row title="界面动效" desc="150–250ms 的过渡；关闭后仅保留状态切换">
                  <Switch on={animations} onChange={setAnimations} />
                </Row>
                <Row title="列表密度" desc="实例与模组列表的行高">
                  <div className="flex items-center gap-1 rounded-sm border border-border p-0.5">
                    {['紧凑', '舒适'].map((v, i) => (
                      <span key={v} className={cx('rounded-sm px-2 py-0.5 text-[10.5px]', i === 1 ? 'bg-white/[0.08] text-tx-1' : 'text-tx-3')}>{v}</span>
                    ))}
                  </div>
                </Row>
              </div>
            </div>
          ) : null}

          {section === 'java' ? (
            <div className="space-y-2.5">
              <Row title="Java 路径" desc="留空则按版本自动选择（1.20.x → 17，1.21+ → 21）">
                <span className="font-mono text-[10.5px] text-tx-2">C:\Program Files\Java\jdk-17\bin\javaw.exe</span>
              </Row>
              <Row title="默认内存" desc="新建实例的初始值">
                <span className="font-mono text-[10.5px] text-tx-2">4096 MB</span>
              </Row>
              <Row title="JVM 参数" desc="追加到所有实例，留空用内置参数">
                <span className="font-mono text-[10.5px] text-tx-3">-XX:+UseG1GC</span>
              </Row>
            </div>
          ) : null}

          {section === 'source' ? (
            <div className="space-y-2.5">
              <Row title="下载源" desc="镜像只做传输替代，所有文件仍按官方 SHA-1 校验">
                <div className="flex items-center gap-1.5">
                  {([['bmclapi', 'BMCLAPI 镜像'], ['official', '官方源']] as const).map(([id, label]) => (
                    <button
                      key={id}
                      type="button"
                      onClick={() => setSource(id)}
                      className={cx(
                        'rounded-sm border px-2 py-1 text-[10.5px] transition-colors',
                        source === id ? 'border-primary/40 bg-primary/10 text-primary' : 'border-border text-tx-3 hover:text-tx-2',
                      )}
                    >
                      {label}
                    </button>
                  ))}
                </div>
              </Row>
              <Row title="最大并发" desc="小文件靠并发堆吞吐；过高会触发限流">
                <span className="font-mono text-[10.5px] text-tx-2">16</span>
              </Row>
              <Row title="CurseForge API Key" desc="填入后才能自动下载 CF 整合包的全部模组">
                <Button variant="ghost" size="sm">填写</Button>
              </Row>
            </div>
          ) : null}

          {section === 'backup' ? (
            <div className="space-y-2.5">
              <Row title="自动备份" desc="启动前与定时两种触发方式">
                <Switch on onChange={() => {}} />
              </Row>
              <Row title="保留份数" desc="超出后删除最旧的一份">
                <span className="font-mono text-[10.5px] text-tx-2">7 份</span>
              </Row>
              <Row title="备份位置" desc="可与实例目录分离，便于放机械盘">
                <span className="font-mono text-[10.5px] text-tx-2">D:\OrapCraft\backups</span>
              </Row>
            </div>
          ) : null}

          {section === 'advanced' ? (
            <div className="space-y-2.5">
              <Row title="数据目录" desc="实例、库、资源缓存都在这里">
                <span className="font-mono text-[10.5px] text-tx-2">D:\OrapCraft</span>
              </Row>
              <Row title="日志级别" desc="影响写入文件的详细程度">
                <span className="font-mono text-[10.5px] text-tx-2">INFO</span>
              </Row>
              <Row title="清理缓存" desc="删除已下载的资源与安装器缓存，不影响实例">
                <Button variant="danger" size="sm" icon={<HardDrive className="h-3 w-3" />}>清理 812 MB</Button>
              </Row>
              <div className="mt-3 flex items-center gap-2 rounded-sm border border-border px-3 py-2 text-[10.5px] text-tx-3">
                <Dot tone="success" /> 配置已保存到 D:\OrapCraft\settings.json
              </div>
            </div>
          ) : null}
        </Card>
      </div>
    </div>
  )
}

function Row({ title, desc, children }: { title: string; desc: string; children: React.ReactNode }) {
  return (
    <div className="flex items-center justify-between gap-4 border-b border-border py-3 last:border-0">
      <div className="min-w-0">
        <div className="text-[12px] text-tx-1">{title}</div>
        <div className="mt-0.5 text-[10.5px] text-tx-3">{desc}</div>
      </div>
      <div className="shrink-0">{children}</div>
    </div>
  )
}

function Switch({ on, onChange }: { on: boolean; onChange: (v: boolean) => void }) {
  return (
    <button
      type="button"
      onClick={() => onChange(!on)}
      aria-pressed={on}
      className={cx(
        'relative h-4 w-8 rounded-full border transition-colors duration-150',
        on ? 'border-primary/50 bg-primary/30' : 'border-border bg-white/[0.06]',
      )}
    >
      <span
        className={cx(
          'absolute top-[2px] h-[10px] w-[10px] rounded-full bg-tx-1 transition-all duration-150',
          on ? 'left-[16px]' : 'left-[2px]',
        )}
      />
    </button>
  )
}