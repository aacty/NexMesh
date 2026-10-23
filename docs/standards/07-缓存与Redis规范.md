# 07 缓存与 Redis 规范

## Key 命名

- **[强制]** 三段式 `服务:实体:标识`,小写、冒号分层:`order-service:order:42f3...`
- **[强制]** 集合类键带维度后缀:`...:list:page1-size20`
- **[禁止]** 中文/空格/无前缀裸键;一个业务实体只用一种键形态(定义 Key 常量类)

## TTL 与一致性(Cache-Aside)

```csharp
var dto = await cache.Get().GetOrCreateAsync(Key(id), TimeSpan.FromMinutes(5),
    async ct => await LoadAsync(id, ct), ct);   // 读

await repo.UpdateAsync(entity);
await cache.Get().RemoveAsync(Key(id));          // 写后失效,不写"更新缓存"
```

- **[强制]** TTL 必填(默认 5 分钟),永久键需 PR 说明
- **[强制]** 读走 `GetOrCreateAsync`(框架保证同 key 单飞防击穿),禁止裸 Get 后自己判空回源
- **[强制]** 写路径先落库后 `RemoveAsync`(失效而非更新,避免并发写脏)
- **[建议]** 热点列表分页缓存 + 短 TTL(30~60s);穿透防护:不存在也缓存空值短 TTL(60s)

## 降级语义

- 未配置 Redis → 内存缓存(单实例语义),测试环境零依赖
- Redis 故障 → 自动降级内存 60s 并告警;**[禁止]** 业务代码感知降级写 if-redis 分支

## 安全与锁

- **[禁止]** 缓存敏感明文(身份证/手机号脱敏后再进缓存;或字段级加密后整体缓存)
- 分布式锁用法见[事务规范 06](06-事务与一致性规范.md)
