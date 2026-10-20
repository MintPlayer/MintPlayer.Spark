import { NodePage, NodeRequest, TreeNode, TreeSelectProvider } from '@mintplayer/ng-bootstrap/tree-select';
import { QueryColumnFilter, QueryResult, QueryResultItem, SparkReferenceTree } from '@mintplayer/ng-spark/models';

/** Rows per request: one "load more" step of the multi-reference panel. */
export const SPARK_REFERENCE_TREE_PAGE_SIZE = 50;

/**
 * Runs one page of a reference query: `skip`/`take`, the optional search term, and, for a hierarchical
 * reference, the column filter that picks one level of the tree.
 */
export type ReferenceQueryPageLoader = (page: { skip: number; take: number; search?: string; columns?: QueryColumnFilter[] }) => Promise<QueryResult>;

/** The chip and option label of a reference candidate: its breadcrumb, or its id when it has none. */
export function referenceTreeNode(item: QueryResultItem): TreeNode | null {
  return item.id ? { id: item.id, label: item.breadcrumb || item.id } : null;
}

/**
 * Whether a row of a hierarchical reference may have children (WX9): `false` only when the declared
 * child-count column says `0` or `false`. Without that column, or without a value in it, every node is
 * expandable and one without children expands to nothing.
 */
export function referenceTreeNodeMayHaveChildren(item: QueryResultItem, tree: SparkReferenceTree): boolean {
  const column = tree.childCountAttribute;
  if (!column) return true;
  const cell = item.values?.find(v => v.key.toLowerCase() === column.toLowerCase());
  const value = cell?.value;
  if (value === undefined || value === null) return true;
  if (typeof value === 'boolean') return value;
  const count = Number(value);
  return Number.isNaN(count) ? true : count > 0;
}

/**
 * The `<bs-tree-select>` data port of a multi-reference, backed by the reference query itself.
 *
 * It replaced an `InMemoryTreeSelectProvider` over the rows the form pre-loaded, which were one page of
 * the query (50 by default): the panel listed and searched only those, so a candidate past the 50th
 * could not be chosen at all, and nothing said the list was cut (see `docs/default_index_PRD.md`, the
 * note beside D10). Here the panel's "load more" is the next server page and its search is the query's
 * `search`, so every row the caller may read is reachable, under the same row security as the list.
 *
 * **Hierarchical references (WX9).** When the attribute declares a `referenceTree`, the roots are the
 * rows whose parent column is empty and a node's children are the rows whose parent column holds its
 * id, both read through the query's own column filter (#431), paged like the flat list. Search stays
 * flat: a hit is listed wherever it sits in the tree. Without `referenceTree` the query is flat and no
 * node has children.
 */
export class ReferenceQueryTreeSelectProvider implements TreeSelectProvider {
  constructor(
    private readonly load: ReferenceQueryPageLoader,
    private readonly pageSize = SPARK_REFERENCE_TREE_PAGE_SIZE,
    private readonly tree?: SparkReferenceTree | null,
  ) {}

  loadRoots(req: NodeRequest): Promise<NodePage> {
    return this.page(req, undefined, this.tree ? [{ name: this.tree.parentAttribute, includes: [null] }] : undefined);
  }

  search(query: string, req: NodeRequest): Promise<NodePage> {
    return this.page(req, query.trim() || undefined);
  }

  /** The children of `parentId`; none for a flat reference, which never draws an expand chevron anyway. */
  loadChildren(parentId: string, req: NodeRequest): Promise<NodePage> {
    if (!this.tree) return Promise.resolve({ nodes: [] });
    return this.page(req, undefined, [{ name: this.tree.parentAttribute, includes: [parentId] }]);
  }

  private async page(req: NodeRequest, search?: string, columns?: QueryColumnFilter[]): Promise<NodePage> {
    const skip = req.offset ?? 0;
    const result = await this.load(columns
      ? { skip, take: this.pageSize, search, columns }
      : { skip, take: this.pageSize, search });
    const items = result.items ?? [];
    const nodes: TreeNode[] = [];
    for (const item of items) {
      const node = referenceTreeNode(item);
      if (!node) continue;
      if (this.tree && referenceTreeNodeMayHaveChildren(item, this.tree)) node.lazy = true;
      nodes.push(node);
    }
    return { nodes, hasMore: skip + items.length < (result.totalItems ?? 0) };
  }
}
